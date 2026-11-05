// Atomic access rights (spec "Permission"; named AccessRight because Permission is the existing RBAC entity).
namespace BlueDrive.Domain.Permissions;

/// <summary>
/// Atomic rights on an item (Space, Library, folder, file), combined into permission levels. Stored as a 64-bit mask
/// (<c>permission_levels.permissions</c>, <c>scope_grants.permissions</c>); the values are part of the database and never change.
/// </summary>
[Flags]
public enum AccessRight : long
{
    None = 0,

    // Xem
    /// <summary>Thấy trong danh sách và xem thông tin.</summary>
    ViewItems = 1L << 0,
    /// <summary>Xem nội dung / xem trước.</summary>
    OpenItems = 1L << 1,
    /// <summary>Tải xuống, đồng bộ xuống máy, WebDAV đọc.</summary>
    Download = 1L << 2,
    Print = 1L << 3,
    /// <summary>Sao chép nội dung trong trình xem.</summary>
    CopyContent = 1L << 4,
    ViewVersions = 1L << 5,
    /// <summary>Thấy bản phụ / bản chưa duyệt.</summary>
    ViewDrafts = 1L << 6,
    /// <summary>Xem ai đã xem / sửa.</summary>
    ViewActivity = 1L << 7,

    // Đóng góp
    /// <summary>Tải lên, tạo mới.</summary>
    AddItems = 1L << 16,
    /// <summary>Sửa nội dung, thông tin, tạo phiên bản.</summary>
    EditItems = 1L << 17,
    /// <summary>Chỉ sửa / xóa tệp do mình tạo (cấp Đóng góp).</summary>
    EditOwnItemsOnly = 1L << 18,
    /// <summary>Đưa vào thùng rác.</summary>
    DeleteItems = 1L << 19,
    RenameMove = 1L << 20,
    CreateFolders = 1L << 21,
    Comment = 1L << 22,
    CheckOutOverride = 1L << 23,
    RestoreVersions = 1L << 24,

    // Chia sẻ / Quản lý
    /// <summary>Chia sẻ cho người trong đơn vị.</summary>
    ShareInternal = 1L << 32,
    /// <summary>Chia sẻ cho khách / liên kết "Bất kỳ ai".</summary>
    ShareExternal = 1L << 33,
    CreateLinks = 1L << 34,
    /// <summary>Thêm / bớt quyền, ngắt kế thừa.</summary>
    ManagePermissions = 1L << 35,
    ApproveItems = 1L << 36,
    /// <summary>Cột, dạng xem, phiên bản, phê duyệt của Library.</summary>
    ManageLibrarySettings = 1L << 37,
    /// <summary>Thành viên, cài đặt, quota, thùng rác tầng 2 của Space.</summary>
    ManageSpace = 1L << 38,
    ManageRetention = 1L << 39,
}

public static class AccessRights
{
    /// <summary>Every atomic right (the "Chủ sở hữu / Quản lý" level, and the owner of a file in Drive của tôi).</summary>
    public const AccessRight All =
        AccessRight.ViewItems | AccessRight.OpenItems | AccessRight.Download | AccessRight.Print | AccessRight.CopyContent | AccessRight.ViewVersions
        | AccessRight.ViewDrafts | AccessRight.ViewActivity
        | AccessRight.AddItems | AccessRight.EditItems | AccessRight.EditOwnItemsOnly | AccessRight.DeleteItems | AccessRight.RenameMove
        | AccessRight.CreateFolders | AccessRight.Comment | AccessRight.CheckOutOverride | AccessRight.RestoreVersions
        | AccessRight.ShareInternal | AccessRight.ShareExternal | AccessRight.CreateLinks | AccessRight.ManagePermissions | AccessRight.ApproveItems
        | AccessRight.ManageLibrarySettings | AccessRight.ManageSpace | AccessRight.ManageRetention;

    /// <summary>Rights that change content, structure or access (taken away by a locked Space, a legal hold…).</summary>
    public const AccessRight Writes =
        AccessRight.AddItems | AccessRight.EditItems | AccessRight.EditOwnItemsOnly | AccessRight.DeleteItems | AccessRight.RenameMove
        | AccessRight.CreateFolders | AccessRight.CheckOutOverride | AccessRight.RestoreVersions | AccessRight.ManagePermissions
        | AccessRight.ApproveItems | AccessRight.ManageLibrarySettings | AccessRight.ManageSpace | AccessRight.ManageRetention;

    public const AccessRight Read =
        AccessRight.ViewItems | AccessRight.OpenItems | AccessRight.Download | AccessRight.Print | AccessRight.CopyContent | AccessRight.ViewVersions;

    public static bool Has(this AccessRight mask, AccessRight wanted) => (mask & wanted) == wanted;

    /// <summary>Vietnamese description of each atomic right (screen "Cấp quyền").</summary>
    public static readonly IReadOnlyDictionary<AccessRight, string> Descriptions = new Dictionary<AccessRight, string>
    {
        [AccessRight.ViewItems] = "Thấy trong danh sách và xem thông tin",
        [AccessRight.OpenItems] = "Xem nội dung, xem trước",
        [AccessRight.Download] = "Tải xuống, đồng bộ xuống máy",
        [AccessRight.Print] = "In",
        [AccessRight.CopyContent] = "Sao chép nội dung trong trình xem",
        [AccessRight.ViewVersions] = "Xem các phiên bản",
        [AccessRight.ViewDrafts] = "Thấy bản nháp, bản chưa duyệt",
        [AccessRight.ViewActivity] = "Xem ai đã xem, đã sửa",
        [AccessRight.AddItems] = "Tải lên, tạo mới",
        [AccessRight.EditItems] = "Sửa nội dung và thông tin, tạo phiên bản",
        [AccessRight.EditOwnItemsOnly] = "Chỉ sửa, xóa tệp do mình tạo",
        [AccessRight.DeleteItems] = "Xóa (đưa vào thùng rác)",
        [AccessRight.RenameMove] = "Đổi tên, di chuyển",
        [AccessRight.CreateFolders] = "Tạo thư mục",
        [AccessRight.Comment] = "Bình luận",
        [AccessRight.CheckOutOverride] = "Hủy lấy ra (check-out) của người khác",
        [AccessRight.RestoreVersions] = "Khôi phục phiên bản",
        [AccessRight.ShareInternal] = "Chia sẻ cho người trong đơn vị",
        [AccessRight.ShareExternal] = "Chia sẻ cho khách, liên kết \"Bất kỳ ai\"",
        [AccessRight.CreateLinks] = "Tạo liên kết chia sẻ",
        [AccessRight.ManagePermissions] = "Thêm, bớt quyền, ngắt kế thừa",
        [AccessRight.ApproveItems] = "Phê duyệt nội dung",
        [AccessRight.ManageLibrarySettings] = "Cài đặt thư viện (cột, dạng xem, phiên bản, phê duyệt)",
        [AccessRight.ManageSpace] = "Quản lý Space (thành viên, cài đặt, quota, thùng rác tầng 2)",
        [AccessRight.ManageRetention] = "Quản lý lưu giữ",
    };
}

/// <summary>The built-in permission levels (spec 3.2). Their ids are fixed: data and tests refer to them.</summary>
public static class SystemLevels
{
    public static readonly Guid Owner = new("0193f000-0000-7000-8000-000000000001");
    public static readonly Guid ContentManager = new("0193f000-0000-7000-8000-000000000002");
    public static readonly Guid Editor = new("0193f000-0000-7000-8000-000000000003");
    public static readonly Guid Contributor = new("0193f000-0000-7000-8000-000000000004");
    public static readonly Guid Commenter = new("0193f000-0000-7000-8000-000000000005");
    public static readonly Guid Viewer = new("0193f000-0000-7000-8000-000000000006");
    public static readonly Guid ViewOnly = new("0193f000-0000-7000-8000-000000000007");
    public static readonly Guid Approver = new("0193f000-0000-7000-8000-000000000008");

    public sealed record Level(Guid Id, string Code, string Name, string Description, AccessRight Permissions, int SortOrder);

    public static readonly IReadOnlyList<Level> All =
    [
        new(Owner, "owner", "Chủ sở hữu / Quản lý", "Toàn quyền.", AccessRights.All, 10),
        new(ContentManager, "content_manager", "Quản lý nội dung",
            "Mọi quyền trừ quản lý Space và lưu giữ; quản lý quyền của thư mục, tệp.",
            AccessRights.All & ~(AccessRight.ManageSpace | AccessRight.ManageRetention), 20),
        new(Editor, "editor", "Chỉnh sửa",
            "Xem đầy đủ, thêm, sửa, xóa, đổi tên, di chuyển, tạo thư mục, bình luận, khôi phục phiên bản, chia sẻ nội bộ.",
            AccessRights.Read | AccessRight.ViewDrafts | AccessRight.AddItems | AccessRight.EditItems | AccessRight.DeleteItems | AccessRight.RenameMove
            | AccessRight.CreateFolders | AccessRight.Comment | AccessRight.RestoreVersions | AccessRight.ShareInternal, 30),
        new(Contributor, "contributor", "Đóng góp", "Xem, thêm mới, chỉ sửa và xóa tệp do mình tạo, bình luận.",
            AccessRights.Read | AccessRight.AddItems | AccessRight.EditOwnItemsOnly | AccessRight.Comment, 40),
        new(Commenter, "commenter", "Người nhận xét", "Xem, tải xuống và bình luận.", AccessRights.Read | AccessRight.Comment, 50),
        new(Viewer, "viewer", "Người xem", "Xem, tải xuống, in, sao chép nội dung, xem phiên bản.", AccessRights.Read, 60),
        new(ViewOnly, "view_only", "Chỉ xem (không tải)",
            "Chỉ xem trong trình xem có hình mờ; không tải xuống, không in, không sao chép, không đồng bộ xuống máy.",
            AccessRight.ViewItems | AccessRight.OpenItems, 70),
        new(Approver, "approver", "Duyệt nội dung", "Người xem, thấy bản chưa duyệt và phê duyệt.",
            AccessRights.Read | AccessRight.ViewDrafts | AccessRight.ApproveItems, 80),
    ];
}
