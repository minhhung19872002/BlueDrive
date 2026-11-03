using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Agent;

/// <summary>
/// Watches the sync folder (FileSystemWatcher / ReadDirectoryChangesW, 64 KB buffer). It only says "something changed": the engine
/// always rescans, so a lost event costs nothing. A buffer overflow raises <see cref="Changed"/> with <c>overflow = true</c>.
/// </summary>
public sealed class LocalChangeWatcher : IDisposable
{
    private readonly FileSystemWatcher _w;

    /// <summary>(relative path or "", overflow).</summary>
    public event Action<string, bool>? Changed;

    public LocalChangeWatcher(string root)
    {
        _w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _w.Created += (_, e) => On(e.FullPath);
        _w.Changed += (_, e) => On(e.FullPath);
        _w.Deleted += (_, e) => On(e.FullPath);
        _w.Renamed += (_, e) =>
        {
            On(e.OldFullPath);
            On(e.FullPath);
        };
        _w.Error += (_, _) => Changed?.Invoke("", true);
        _w.EnableRaisingEvents = true;
    }

    private void On(string full)
    {
        var rel = Path.GetRelativePath(_w.Path, full).Replace(Path.DirectorySeparatorChar, '/');
        // Our own temporary files and the editors' lock / temp files do not need a sync round.
        if (rel.Split('/').Any(IgnoreRules.IsIgnored)) return;
        Changed?.Invoke(rel, false);
    }

    public void Dispose() => _w.Dispose();
}
