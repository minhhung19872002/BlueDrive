using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Agent;

/// <summary>Maps a path in the sync folder to its BlueDrive item (Explorer context menu: share, link, versions…).</summary>
public static class LocalIndex
{
    public sealed record Hit(RootState Root, BaseItem Item, string RelativePath);

    public static Hit? Find(SyncStateDb db, string syncFolder, string absPath)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(syncFolder), Path.GetFullPath(absPath)).Replace(Path.DirectorySeparatorChar, '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        foreach (var root in db.Roots().Where(r => r.Enabled).OrderByDescending(r => r.LocalPath.Length))
        {
            string inRoot;
            if (root.LocalPath.Length == 0) inRoot = rel;
            else if (rel.StartsWith(root.LocalPath + "/", StringComparison.OrdinalIgnoreCase)) inRoot = rel[(root.LocalPath.Length + 1)..];
            else continue;
            var items = db.Items(root.RootId);
            var byRemote = items.ToDictionary(i => i.RemoteId);
            foreach (var b in items)
                if (string.Equals(PathOf(b, byRemote), inRoot, StringComparison.OrdinalIgnoreCase)) return new Hit(root, b, inRoot);
            return null;
        }
        return null;
    }

    /// <summary>Where the BlueDrive item <paramref name="remoteId"/> is on this computer (null when it is not synced here).</summary>
    public static string? AbsPathOf(SyncStateDb db, string syncFolder, string remoteId)
    {
        foreach (var root in db.Roots().Where(r => r.Enabled))
        {
            var byRemote = db.Items(root.RootId).ToDictionary(i => i.RemoteId);
            if (!byRemote.TryGetValue(remoteId, out var b)) continue;
            var rel = PathOf(b, byRemote);
            var full = root.LocalPath.Length == 0 ? rel : root.LocalPath + "/" + rel;
            return Path.Combine(Path.GetFullPath(syncFolder), full.Replace('/', Path.DirectorySeparatorChar));
        }
        return null;
    }

    internal static string PathOf(BaseItem b, IReadOnlyDictionary<string, BaseItem> byRemote)
    {
        var parts = new List<string> { b.Name };
        var cur = b.ParentRemoteId;
        for (var guard = 0; cur is not null && guard < 256 && byRemote.TryGetValue(cur, out var p); guard++)
        {
            parts.Add(p.Name);
            cur = p.ParentRemoteId;
        }
        parts.Reverse();
        return string.Join('/', parts);
    }
}
