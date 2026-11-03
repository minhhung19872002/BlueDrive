using System.Security.Principal;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace BlueDrive.Sync.Windows;

public sealed record SyncRootInfo(string Path, Guid AccountId, string DisplayName, string Version, string IconResource, Uri? RecycleBin);

/// <summary>
/// Registers the account's BlueDrive folder as a Windows sync root: Explorer then shows it in the navigation pane with the cloud status
/// column and the "Luôn giữ trên thiết bị / Giải phóng dung lượng" commands. Uses StorageProviderSyncRootManager (shell integration) and
/// falls back to the bare cloud filter registration (CfRegisterSyncRoot) when the shell API is not available to the process.
/// </summary>
public static class SyncRootRegistrar
{
    /// <summary>Fixed id of the BlueDrive cloud files provider.</summary>
    public static readonly Guid ProviderId = new("3c6d3a4e-7f0b-4b8e-9d2a-5b1e0d1e0001");

    public static string Id(Guid accountId) => $"BlueDrive!{WindowsIdentity.GetCurrent().User?.Value ?? "user"}!{accountId:N}";

    /// <summary>Returns true when the shell registration (navigation pane) succeeded, false when only the cloud filter is registered.</summary>
    public static async Task<bool> RegisterAsync(SyncRootInfo info)
    {
        Directory.CreateDirectory(info.Path);
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(info.Path));
            var root = new StorageProviderSyncRootInfo
            {
                Id = Id(info.AccountId),
                Path = folder,
                DisplayNameResource = info.DisplayName,
                IconResource = info.IconResource,
                HydrationPolicy = StorageProviderHydrationPolicy.Full,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime
                               | StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                ShowSiblingsAsGroup = false,
                Version = info.Version,
                AllowPinning = true,
                ProtectionMode = StorageProviderProtectionMode.Unknown,
                Context = CryptographicBuffer.ConvertStringToBinary(info.AccountId.ToString("N"), BinaryStringEncoding.Utf8),
            };
            if (info.RecycleBin is not null) root.RecycleBinUri = info.RecycleBin;
            StorageProviderSyncRootManager.Register(root);
            // Windows accepts the call but ignores some folders (e.g. under %TEMP%): check it really took.
            if (StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder)?.Id == root.Id) return true;
            RegisterFilterOnly(info);
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            RegisterFilterOnly(info);
            return false;
        }
    }

    private static unsafe void RegisterFilterOnly(SyncRootInfo info)
    {
        var identity = Encoding.UTF8.GetBytes(info.AccountId.ToString("N"));
        fixed (char* name = "BlueDrive")
        fixed (char* version = info.Version)
        fixed (byte* id = identity)
        {
            var reg = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION), ProviderName = name, ProviderVersion = version,
                SyncRootIdentity = id, SyncRootIdentityLength = (uint)identity.Length, ProviderId = ProviderId,
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED,
                },
                Population = new CF_POPULATION_POLICY { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
            };
            CloudNative.Check(PInvoke.CfRegisterSyncRoot(Path.GetFullPath(info.Path), reg, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE), "Đăng ký thư mục đồng bộ");
        }
    }

    /// <summary>Sign out / remote wipe: Explorer forgets the sync root (files left behind are plain files).</summary>
    public static void Unregister(SyncRootInfo info)
    {
        try { StorageProviderSyncRootManager.Unregister(Id(info.AccountId)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        PInvoke.CfUnregisterSyncRoot(Path.GetFullPath(info.Path));
    }

    /// <summary>Id of the sync root the folder belongs to (as Windows sees it), or null.</summary>
    public static async Task<string?> RootIdForFolderAsync(string path)
    {
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(path));
            return StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder)?.Id;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException) { return null; }
    }

    public static bool IsRegistered(Guid accountId)
    {
        try { return StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(r => r.Id == Id(accountId)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return false; }
    }
}
