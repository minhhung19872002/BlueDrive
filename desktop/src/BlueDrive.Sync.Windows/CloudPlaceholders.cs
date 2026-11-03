using System.Text;
using BlueDrive.Sync.Core;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace BlueDrive.Sync.Windows;

/// <summary>
/// Files On-Demand placeholders (Cloud Files API): server files appear with their real size and date but take no space until opened;
/// the file identity is the BlueDrive item id, so hydration can fetch the content later. Paths are relative to the sync folder.
/// </summary>
public sealed class CloudPlaceholders(string root) : IPlaceholderProvider
{
    private readonly string _root = Path.GetFullPath(root);

    public bool OnDemand => true;

    private string Abs(string path) => Path.Combine(_root, path.Replace('/', '\\'));

    public Task CreatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct)
    {
        Create(Abs(path), item);
        return Task.CompletedTask;
    }

    private static unsafe void Create(string abs, RemoteItem item)
    {
        var dir = Path.GetDirectoryName(abs)!;
        var name = Path.GetFileName(abs);
        var identity = Encoding.UTF8.GetBytes(item.Id);
        fixed (char* n = name)
        fixed (byte* id = identity)
        {
            var info = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = n, FsMetadata = Metadata(item), FileIdentity = id, FileIdentityLength = (uint)identity.Length,
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                        | (item.IsFolder ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION : 0),
            };
            CloudNative.Check(PInvoke.CfCreatePlaceholders(dir, new ReadOnlySpan<CF_PLACEHOLDER_CREATE_INFO>(&info, 1), CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _),
                $"Tạo tệp giữ chỗ \"{name}\"");
            CloudNative.Check(info.Result, $"Tạo tệp giữ chỗ \"{name}\"");
        }
    }

    private static CF_FS_METADATA Metadata(RemoteItem item)
    {
        var t = CloudNative.FileTime(item.ModifiedAt);
        return new CF_FS_METADATA
        {
            BasicInfo = new FILE_BASIC_INFO
            {
                CreationTime = t, LastAccessTime = t, LastWriteTime = t, ChangeTime = t,
                FileAttributes = (uint)(item.IsFolder ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY
                    : item.CanEdit ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_READONLY),
            },
            FileSize = item.IsFolder ? 0 : item.Size,
        };
    }

    /// <summary>
    /// New server version of an unchanged local file: the placeholder takes the new size / date and drops the old content; a file the user
    /// keeps on the device ("Luôn giữ trên thiết bị") is downloaded again right away.
    /// </summary>
    public Task UpdatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct)
    {
        var abs = Abs(path);
        var pinned = File.GetAttributes(abs).HasFlag(CloudNative.Pinned);
        using (var h = CloudNative.OpenForPlaceholder(abs))
        {
            if (pinned) PInvoke.CfSetPinState(h, CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED, CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE);
            var hr = PInvoke.CfUpdatePlaceholder(h, Metadata(item), Encoding.UTF8.GetBytes(item.Id), default,
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC);
            if (hr.Value == CloudNative.NotACloudFile)
            {
                // A plain file (created before the folder became a sync root): replace it by a placeholder.
                h.Dispose();
                File.Delete(abs);
                Create(abs, item);
            }
            else CloudNative.Check(hr, $"Cập nhật tệp giữ chỗ \"{path}\"");
        }
        if (pinned) Pin(abs, true);
        return Task.CompletedTask;
    }

    /// <summary>After a sync step: the item matches <paramref name="remoteId"/> (green tick in Explorer); plain files / folders become placeholders.</summary>
    public Task MarkInSyncAsync(string path, string remoteId, CancellationToken ct)
    {
        var abs = Abs(path);
        using var h = CloudNative.OpenForPlaceholder(abs);
        var identity = Encoding.UTF8.GetBytes(remoteId);
        var hr = PInvoke.CfUpdatePlaceholder(h, null, identity, default, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC);
        if (hr.Value == CloudNative.NotACloudFile)
        {
            var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC;
            if (Directory.Exists(abs)) flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ALWAYS_FULL;
            hr = PInvoke.CfConvertToPlaceholder(h, identity, flags);
        }
        CloudNative.Check(hr, $"Đánh dấu đã đồng bộ \"{path}\"");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns every downloaded placeholder under <paramref name="root"/> back into a plain file, then the folders (deepest first). Items
    /// that are not placeholders, still dehydrated, or open elsewhere are left as they are.
    /// </summary>
    public static int RevertAll(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var reverted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
            if (!IsDehydrated(file) && Revert(file)) reverted++;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length))
            if (Revert(dir)) reverted++;
        return reverted;
    }

    private static bool Revert(string abs)
    {
        try
        {
            if (!IsPlaceholder(abs)) return false;
            using var h = CloudNative.OpenForPlaceholder(abs);
            return PInvoke.CfRevertPlaceholder(h, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE).Succeeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The item is a cloud placeholder (the filter hides the reparse point from ordinary file attributes).</summary>
    public static unsafe bool IsPlaceholder(string abs)
    {
        using var h = CloudNative.OpenForPlaceholder(abs);
        var info = default(CF_PLACEHOLDER_BASIC_INFO);
        var hr = PInvoke.CfGetPlaceholderInfo((global::Windows.Win32.Foundation.HANDLE)h.DangerousGetHandle(), CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
            &info, (uint)sizeof(CF_PLACEHOLDER_BASIC_INFO), null);
        return hr.Succeeded || hr.Value == unchecked((int)0x800700EA); // more data: the identity did not fit, it is a placeholder
    }

    /// <summary>Explorer shows the item as synced (not "Đang chờ đồng bộ").</summary>
    public static unsafe bool IsInSync(string abs)
    {
        using var h = CloudNative.OpenForPlaceholder(abs);
        var info = default(CF_PLACEHOLDER_BASIC_INFO);
        var hr = PInvoke.CfGetPlaceholderInfo((global::Windows.Win32.Foundation.HANDLE)h.DangerousGetHandle(), CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
            &info, (uint)sizeof(CF_PLACEHOLDER_BASIC_INFO), null);
        // A larger structure (with the identity) does not fit: the fixed part is filled all the same.
        return (hr.Succeeded || hr.Value == unchecked((int)0x800700EA)) && info.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC;
    }

    /// <summary>"Luôn giữ trên thiết bị" (download now and keep) / back to on-demand.</summary>
    public static void Pin(string abs, bool keep)
    {
        using var h = CloudNative.OpenForPlaceholder(abs);
        CloudNative.Check(PInvoke.CfSetPinState(h, keep ? CF_PIN_STATE.CF_PIN_STATE_PINNED : CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            Directory.Exists(abs) ? CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_RECURSE : CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE), "Đổi trạng thái giữ trên thiết bị");
        if (keep && File.Exists(abs)) _ = Task.Run(() => Hydrate(abs));
    }

    /// <summary>Downloads the whole content now (the filter calls back into the provider).</summary>
    public static void Hydrate(string abs)
    {
        using var h = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).SafeFileHandle;
        CloudNative.Check(PInvoke.CfHydratePlaceholder(h, 0, -1, CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE), "Tải tệp xuống máy");
    }

    /// <summary>"Giải phóng dung lượng": keeps the placeholder, drops the content (only for files already on the server).</summary>
    /// <summary>
    /// Frees the content (the file stays as a placeholder). Just after a download, or while an antivirus / the indexer still reads the
    /// file, Windows can refuse for a moment (sharing violation, "not in sync"): retried a few times before giving up.
    /// </summary>
    public static void Dehydrate(string abs)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var h = CloudNative.OpenForPlaceholder(abs);
                CloudNative.Check(PInvoke.CfDehydratePlaceholder(h, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE), "Giải phóng dung lượng");
                return;
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(250 * attempt); }
        }
    }

    public static bool IsDehydrated(string abs) => File.Exists(abs) && File.GetAttributes(abs).HasFlag(CloudNative.RecallOnDataAccess);
}
