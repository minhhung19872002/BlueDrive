namespace BlueDrive.Sync.Core;

public enum LocalDelta { Unchanged, Modified, Deleted, New, Absent }

public enum RemoteDelta { Unchanged, Modified, Deleted, New, Absent, AccessRevoked }

/// <summary>Facts about one item for the 3-way merge (base = last synced state).</summary>
public sealed record ItemFacts(
    bool IsFolder,
    LocalDelta Local,
    bool LocalMoved,
    RemoteDelta Remote,
    bool RemoteMoved,
    bool SameContent = false,
    bool SameLocation = false,
    bool CanEdit = true,
    bool RemoteFolderKeepsChildren = false);

[Flags]
public enum SyncAction
{
    None = 0,
    /// <summary>Push the local content as a new version (If-Match base revision).</summary>
    UploadVersion = 1 << 0,
    /// <summary>Pull the server content (or refresh the placeholder) over an unmodified local copy.</summary>
    Download = 1 << 1,
    /// <summary>Both sides already agree: only record the new base.</summary>
    Converge = 1 << 2,
    /// <summary>Keep the server copy under the original name, rename the local copy to a conflict name and upload it.</summary>
    Conflict = 1 << 3,
    UploadNew = 1 << 4,
    /// <summary>Create the item locally (download, placeholder or folder).</summary>
    CreateLocal = 1 << 5,
    MoveRemote = 1 << 6,
    MoveLocal = 1 << 7,
    /// <summary>Move to the server trash.</summary>
    DeleteRemote = 1 << 8,
    /// <summary>Delete the local copy (recycle bin).</summary>
    DeleteLocal = 1 << 9,
    ForgetBase = 1 << 10,
    /// <summary>The local copy was deleted but the server copy changed: bring it back instead of deleting on the server.</summary>
    RestoreLocal = 1 << 11,
    /// <summary>The server copy was deleted but the local copy changed: upload it again as a new file.</summary>
    ReuploadAsNew = 1 << 12,
    /// <summary>Changes that cannot be pushed (no edit right, access revoked): moved to "_Không đồng bộ được".</summary>
    Quarantine = 1 << 13,
}

public sealed record SyncDecision(SyncAction Actions, string Reason)
{
    public bool Has(SyncAction a) => (Actions & a) == a;
}

/// <summary>
/// Pure 3-way reconciliation of one item: (base, local, remote) → actions. The full decision table of spec 6.3 is documented in
/// desktop/docs/sync-algorithm.md and covered row by row by ReconcilerTests. Rules of thumb: never lose data (conflict copies,
/// restore instead of delete), remote wins on competing renames, deletions only when the other side did not change.
/// </summary>
public static class Reconciler
{
    public static SyncDecision Decide(ItemFacts f)
    {
        // Items unknown to the base.
        if (f.Local == LocalDelta.New && f.Remote == RemoteDelta.New)
            return f.IsFolder || f.SameContent
                ? new(SyncAction.Converge, "Tạo mới ở cả hai phía với cùng nội dung: liên kết, không tải lại.")
                : new(SyncAction.Conflict, "Tạo mới ở cả hai phía cùng tên nhưng khác nội dung: tạo bản xung đột.");
        if (f.Local == LocalDelta.New) return new(SyncAction.UploadNew, "Tạo mới trên máy: tải lên.");
        if (f.Remote == RemoteDelta.New) return new(SyncAction.CreateLocal, "Tạo mới trên máy chủ: tạo trên máy.");
        if (f.Local == LocalDelta.Absent || f.Remote == RemoteDelta.Absent) return new(SyncAction.None, "Không có ở phía nào.");

        // Access revoked: nothing can be pushed any more.
        if (f.Remote == RemoteDelta.AccessRevoked)
            return f.Local switch
            {
                LocalDelta.Deleted => new(SyncAction.ForgetBase, "Mất quyền truy cập, bản trên máy đã xóa."),
                LocalDelta.Modified => new(SyncAction.Quarantine | SyncAction.ForgetBase, "Mất quyền truy cập khi còn thay đổi chưa đẩy lên: chuyển vào _Không đồng bộ được."),
                _ => new(SyncAction.DeleteLocal | SyncAction.ForgetBase, "Mất quyền truy cập: gỡ khỏi máy."),
            };

        // Deletions.
        if (f.Local == LocalDelta.Deleted && f.Remote == RemoteDelta.Deleted) return new(SyncAction.ForgetBase, "Đã xóa ở cả hai phía.");
        if (f.Local == LocalDelta.Deleted)
        {
            if (f.Remote == RemoteDelta.Modified || f.RemoteMoved)
                return new(SyncAction.RestoreLocal, "Xóa trên máy nhưng máy chủ vừa thay đổi: khôi phục bản máy chủ, không xóa.");
            if (f.IsFolder && f.RemoteFolderKeepsChildren)
                return new(SyncAction.RestoreLocal, "Xóa thư mục trên máy nhưng máy chủ có tệp mới bên trong: khôi phục thư mục.");
            return new(SyncAction.DeleteRemote | SyncAction.ForgetBase, "Xóa trên máy: chuyển vào thùng rác máy chủ.");
        }
        if (f.Remote == RemoteDelta.Deleted)
            return f.Local == LocalDelta.Modified
                ? new(SyncAction.ReuploadAsNew, "Máy chủ đã xóa nhưng bản trên máy vừa sửa: tải lên lại thành tệp mới.")
                : new(SyncAction.DeleteLocal | SyncAction.ForgetBase, "Máy chủ đã xóa: xóa trên máy (vào Thùng rác).");

        // Location: remote wins when both sides moved to different places.
        var location = (f.LocalMoved, f.RemoteMoved) switch
        {
            (true, false) => SyncAction.MoveRemote,
            (false, true) => SyncAction.MoveLocal,
            (true, true) => f.SameLocation ? SyncAction.Converge : SyncAction.MoveLocal,
            _ => SyncAction.None,
        };
        var moveReason = (f.LocalMoved, f.RemoteMoved, f.SameLocation) switch
        {
            (true, true, false) => " Đổi tên/di chuyển ở cả hai phía: ưu tiên máy chủ.",
            (true, false, _) => " Đổi tên/di chuyển trên máy: cập nhật lên máy chủ.",
            (false, true, _) => " Đổi tên/di chuyển trên máy chủ: cập nhật trên máy.",
            _ => "",
        };

        // Content.
        var content = (f.Local, f.Remote) switch
        {
            (LocalDelta.Unchanged, RemoteDelta.Unchanged) => (SyncAction.None, "Không thay đổi."),
            (LocalDelta.Modified, RemoteDelta.Unchanged) => f.CanEdit
                ? (SyncAction.UploadVersion, "Sửa trên máy: tải lên phiên bản mới.")
                : (SyncAction.Quarantine | SyncAction.Download, "Sửa trên máy nhưng không có quyền chỉnh sửa: giữ bản sửa ở _Không đồng bộ được, khôi phục bản máy chủ."),
            (LocalDelta.Unchanged, RemoteDelta.Modified) => (SyncAction.Download, "Sửa trên máy chủ: cập nhật trên máy."),
            (LocalDelta.Modified, RemoteDelta.Modified) when f.SameContent => (SyncAction.Converge, "Sửa ở cả hai phía với cùng nội dung: chỉ cập nhật trạng thái."),
            (LocalDelta.Modified, RemoteDelta.Modified) => f.CanEdit
                ? (SyncAction.Conflict, "Sửa ở cả hai phía khác nhau: giữ bản máy chủ, tạo bản xung đột từ bản trên máy.")
                : (SyncAction.Quarantine | SyncAction.Download, "Sửa ở cả hai phía, không có quyền chỉnh sửa: giữ bản sửa ở _Không đồng bộ được."),
            _ => (SyncAction.None, "Không thay đổi."),
        };
        if (f.IsFolder) content = (SyncAction.None, "");
        var actions = content.Item1 | location;
        if (actions == SyncAction.Converge || (location == SyncAction.Converge && content.Item1 == SyncAction.None)) actions = SyncAction.Converge;
        return new(actions, (content.Item2 + moveReason).Trim());
    }
}
