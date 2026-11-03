using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BlueDrive.Sync.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace BlueDrive.Sync.Windows;

/// <summary>
/// Serves the cloud filter for one connected sync root: hydration (FETCH_DATA streamed from the server in 4 KB aligned chunks with
/// progress), cancellation, and delete / rename notifications (refused for view-only items unless BlueDrive itself does them).
/// </summary>
internal sealed class CloudProvider : IDisposable
{
    private const int Chunk = 4 * 1024 * 1024; // multiple of 4 KB
    private readonly string _root;
    private readonly IRemoteRangeReader _content;
    private readonly Func<string, bool> _canModify;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _transfers = new();
    private GCHandle _self;
    private unsafe CF_CALLBACK_REGISTRATION* _table;
    private CF_CONNECTION_KEY _key;
    private bool _connected;

    /// <summary>(relative path) after a file opened for writing is closed.</summary>
    public event Action<string>? FileClosed;
    /// <summary>(relative path, error): a hydration failed (shown in "Vấn đề").</summary>
    public event Action<string, string>? HydrationFailed;

    public int Hydrations;

    public CloudProvider(string root, IRemoteRangeReader content, Func<string, bool> canModify)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _content = content;
        _canModify = canModify;
    }

    public unsafe void Connect()
    {
        _self = GCHandle.Alloc(this);
        _table = (CF_CALLBACK_REGISTRATION*)NativeMemory.AllocZeroed((nuint)(sizeof(CF_CALLBACK_REGISTRATION) * 6));
        _table[0] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = &OnFetchData };
        _table[1] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = &OnCancelFetchData };
        _table[2] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = &OnNotifyDelete };
        _table[3] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = &OnNotifyRename };
        _table[4] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION, Callback = &OnCloseCompletion };
        _table[5] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE };
        CF_CONNECTION_KEY key;
        fixed (char* path = _root)
        {
            var hr = PInvoke.CfConnectSyncRoot(path, _table, (void*)GCHandle.ToIntPtr(_self),
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH, &key);
            CloudNative.Check(hr, "Kết nối thư mục đồng bộ với Windows");
        }
        _key = key;
        _connected = true;
    }

    public unsafe void Dispose()
    {
        if (_connected)
        {
            PInvoke.CfDisconnectSyncRoot(_key);
            _connected = false;
        }
        foreach (var t in _transfers.Values) t.Cancel();
        if (_table is not null)
        {
            NativeMemory.Free(_table);
            _table = null;
        }
        if (_self.IsAllocated) _self.Free();
    }

    private static unsafe CloudProvider From(CF_CALLBACK_INFO* info) => (CloudProvider)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;

    private static unsafe string Identity(CF_CALLBACK_INFO* info) =>
        info->FileIdentity is null || info->FileIdentityLength == 0 ? "" : Encoding.UTF8.GetString((byte*)info->FileIdentity, (int)info->FileIdentityLength);

    /// <summary>NormalizedPath is volume relative ("\Users\...\BlueDrive\a.txt"): make it relative to the sync root ("a/b.txt").</summary>
    private string Relative(string normalized)
    {
        var rootNoDrive = _root.Length > 2 && _root[1] == ':' ? _root[2..] : _root;
        var rel = normalized.StartsWith(rootNoDrive, StringComparison.OrdinalIgnoreCase) ? normalized[rootNoDrive.Length..] : normalized;
        return rel.TrimStart('\\').Replace('\\', '/');
    }

    private unsafe bool IsOwnProcess(CF_CALLBACK_INFO* info) => info->ProcessInfo is not null && info->ProcessInfo->ProcessId == (uint)Environment.ProcessId;

    // ---------------------------------------------------------------- hydration

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* p)
    {
        var self = From(info);
        var req = new FetchRequest(info->ConnectionKey, info->TransferKey, info->RequestKey, Identity(info), info->FileSize,
            p->FetchData.RequiredFileOffset, p->FetchData.RequiredLength, self.Relative(info->NormalizedPath.ToString()));
        _ = Task.Run(() => self.HydrateAsync(req));
    }

    private sealed record FetchRequest(CF_CONNECTION_KEY Key, long TransferKey, long RequestKey, string ItemId, long FileSize, long Offset, long Length, string Path);

    private async Task HydrateAsync(FetchRequest r)
    {
        using var cts = new CancellationTokenSource();
        _transfers[r.TransferKey] = cts;
        var buffer = new byte[Chunk];
        var filled = 0;
        var position = r.Offset;
        var end = Math.Min(r.FileSize, r.Offset + r.Length);
        try
        {
            if (r.ItemId.Length == 0) throw new IOException("Tệp giữ chỗ không có mã tệp BlueDrive.");
            await _content.ReadRangeAsync(r.ItemId, r.Offset, end - r.Offset, (data, ct) =>
            {
                var span = data.Span;
                while (span.Length > 0)
                {
                    var n = Math.Min(span.Length, buffer.Length - filled);
                    span[..n].CopyTo(buffer.AsSpan(filled));
                    filled += n;
                    span = span[n..];
                    if (filled == buffer.Length)
                    {
                        Transfer(r, buffer, position, filled, CloudNative.STATUS_SUCCESS);
                        position += filled;
                        filled = 0;
                        PInvoke.CfReportProviderProgress(r.Key, r.TransferKey, end - r.Offset, position - r.Offset);
                    }
                }
                return ValueTask.CompletedTask;
            }, cts.Token);
            if (filled > 0) Transfer(r, buffer, position, filled, CloudNative.STATUS_SUCCESS);
            Interlocked.Increment(ref Hydrations);
        }
        catch (Exception ex)
        {
            // Tell the filter the rest of the range failed: the application gets an error instead of waiting forever.
            var remaining = end - position;
            if (remaining > 0)
            {
                try { Transfer(r, null, position, remaining, CloudNative.STATUS_CLOUD_FILE_UNSUCCESSFUL); }
                catch (IOException) { }
            }
            if (ex is not OperationCanceledException) HydrationFailed?.Invoke(r.Path, ex.Message);
        }
        finally { _transfers.TryRemove(r.TransferKey, out _); }
    }

    private static unsafe void Transfer(FetchRequest r, byte[]? data, long offset, long length, uint status)
    {
        fixed (byte* buf = data)
        {
            var op = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = r.Key, TransferKey = r.TransferKey, RequestKey = r.RequestKey,
            };
            var param = new CF_OPERATION_PARAMETERS { ParamSize = ParamSize<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct>() };
            param.TransferData.CompletionStatus = new NTSTATUS(unchecked((int)status));
            param.TransferData.Buffer = buf;
            param.TransferData.Offset = offset;
            param.TransferData.Length = length;
            CloudNative.Check(PInvoke.CfExecute(op, ref param), "Chuyển dữ liệu tệp");
        }
    }

    /// <summary>CF_SIZE_OF_OP_PARAM: header up to the union + the member used.</summary>
    private static unsafe uint ParamSize<T>() where T : unmanaged
    {
        CF_OPERATION_PARAMETERS probe = default;
        var offset = (byte*)&probe.Anonymous - (byte*)&probe;
        return (uint)(offset + sizeof(T));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* p)
    {
        if (From(info)._transfers.TryGetValue(info->TransferKey, out var cts)) cts.Cancel();
    }

    // ---------------------------------------------------------------- delete / rename

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* p)
    {
        var self = From(info);
        var allowed = self.IsOwnProcess(info) || self._canModify(Identity(info));
        Ack(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, allowed);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* p)
    {
        var self = From(info);
        var allowed = self.IsOwnProcess(info) || self._canModify(Identity(info));
        Ack(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, allowed);
    }

    private static unsafe void Ack(CF_CALLBACK_INFO* info, CF_OPERATION_TYPE type, bool allowed)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = type, ConnectionKey = info->ConnectionKey, TransferKey = info->TransferKey,
            RequestKey = info->RequestKey,
        };
        var param = new CF_OPERATION_PARAMETERS();
        var status = new NTSTATUS(unchecked((int)(allowed ? CloudNative.STATUS_SUCCESS : CloudNative.STATUS_ACCESS_DENIED)));
        if (type == CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE)
        {
            param.ParamSize = ParamSize<CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckDelete_e__Struct>();
            param.AckDelete.CompletionStatus = status;
        }
        else
        {
            param.ParamSize = ParamSize<CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckRename_e__Struct>();
            param.AckRename.CompletionStatus = status;
        }
        PInvoke.CfExecute(op, ref param);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnCloseCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* p)
    {
        var self = From(info);
        var path = self.Relative(info->NormalizedPath.ToString());
        _ = Task.Run(() => self.FileClosed?.Invoke(path));
    }
}
