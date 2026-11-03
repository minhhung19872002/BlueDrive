namespace BlueDrive.Application.Security;

/// <summary>
/// Functional permission catalog ("function.action"), seeded into the permissions table at startup.
/// Actions map to the 7 matrix columns of HSMT #8: Xem, Tải lên/Thêm mới, Chỉnh sửa, Xóa, Chia sẻ, Phê duyệt, Quản trị.
/// </summary>
public static class Perm
{
    public static class Actions
    {
        public const string View = "view", Upload = "upload", Edit = "edit", Delete = "delete", Share = "share", Approve = "approve", Admin = "admin";
        public static readonly (string Code, string Name)[] All =
        [
            (View, "Xem"), (Upload, "Tải lên / Thêm mới"), (Edit, "Chỉnh sửa"), (Delete, "Xóa"),
            (Share, "Chia sẻ"), (Approve, "Phê duyệt"), (Admin, "Quản trị"),
        ];
    }

    public const string OrgView = "org.view", OrgCreate = "org.upload", OrgEdit = "org.edit", OrgDelete = "org.delete";
    public const string UserView = "user.view", UserCreate = "user.upload", UserEdit = "user.edit", UserDelete = "user.delete", UserAdmin = "user.admin";
    public const string GroupView = "usergroup.view", GroupCreate = "usergroup.upload", GroupEdit = "usergroup.edit", GroupDelete = "usergroup.delete";
    public const string RoleView = "role.view", RoleCreate = "role.upload", RoleEdit = "role.edit", RoleDelete = "role.delete", RoleAdmin = "role.admin";
    public const string CatalogView = "catalog.view", CatalogCreate = "catalog.upload", CatalogEdit = "catalog.edit", CatalogDelete = "catalog.delete";
    public const string AuditView = "audit.view", AuditAdmin = "audit.admin";
    public const string StatsView = "stats.view";
    public const string AlertView = "alert.view", AlertEdit = "alert.edit";
    public const string SessionView = "session.view", SessionAdmin = "session.admin";
    public const string DeviceView = "device.view", DeviceAdmin = "device.admin";
    public const string SettingsView = "settings.view", SettingsEdit = "settings.edit";
    public const string BackupView = "backup.view", BackupAdmin = "backup.admin";
    public const string TicketView = "ticket.view", TicketCreate = "ticket.upload", TicketEdit = "ticket.edit", TicketAdmin = "ticket.admin";
    public const string ChangeLogView = "changelog.view", ChangeLogEdit = "changelog.edit";
    public const string DataExportAdmin = "dataexport.admin";
    public const string DocView = "doc.view", DocUpload = "doc.upload", DocEdit = "doc.edit", DocDelete = "doc.delete",
        DocShare = "doc.share", DocApprove = "doc.approve", DocAdmin = "doc.admin";
    public const string WorkspaceView = "workspace.view", WorkspaceCreate = "workspace.upload", WorkspaceEdit = "workspace.edit",
        WorkspaceDelete = "workspace.delete", WorkspaceAdmin = "workspace.admin";
    public const string TrashView = "trash.view", TrashRestore = "trash.edit", TrashPurge = "trash.delete";
    public const string SearchView = "search.view";
    public const string DashboardAdminView = "dashboard.view";

    public sealed record Definition(string Code, string Module, string ModuleName, string Function, string FunctionName, string Action, string Name, int SortOrder);

    private static readonly (string Module, string ModuleName, string Function, string FunctionName, string[] Actions)[] Functions =
    [
        ("admin", "Quản trị hệ thống", "org", "Cơ cấu tổ chức", ["view", "upload", "edit", "delete"]),
        ("admin", "Quản trị hệ thống", "user", "Người dùng", ["view", "upload", "edit", "delete", "admin"]),
        ("admin", "Quản trị hệ thống", "usergroup", "Nhóm người dùng", ["view", "upload", "edit", "delete"]),
        ("admin", "Quản trị hệ thống", "role", "Vai trò & phân quyền", ["view", "upload", "edit", "delete", "admin"]),
        ("admin", "Quản trị hệ thống", "catalog", "Danh mục dùng chung", ["view", "upload", "edit", "delete"]),
        ("monitor", "Giám sát hệ thống", "audit", "Nhật ký hoạt động", ["view", "admin"]),
        ("monitor", "Giám sát hệ thống", "stats", "Thống kê dung lượng", ["view"]),
        ("monitor", "Giám sát hệ thống", "alert", "Cảnh báo bất thường", ["view", "edit"]),
        ("monitor", "Giám sát hệ thống", "session", "Phiên đăng nhập", ["view", "admin"]),
        ("monitor", "Giám sát hệ thống", "device", "Thiết bị đồng bộ (BlueDrive Desktop)", ["view", "admin"]),
        ("monitor", "Giám sát hệ thống", "dashboard", "Dashboard quản trị", ["view"]),
        ("ops", "Vận hành", "settings", "Cấu hình hệ thống", ["view", "edit"]),
        ("ops", "Vận hành", "backup", "Sao lưu & phục hồi", ["view", "admin"]),
        ("ops", "Vận hành", "ticket", "Hỗ trợ & sự cố", ["view", "upload", "edit", "admin"]),
        ("ops", "Vận hành", "changelog", "Nhật ký thay đổi", ["view", "edit"]),
        ("ops", "Vận hành", "dataexport", "Xuất toàn bộ dữ liệu", ["admin"]),
        ("storage", "Lưu trữ tư liệu", "doc", "Tư liệu (thư mục/tệp)", ["view", "upload", "edit", "delete", "share", "approve", "admin"]),
        ("storage", "Lưu trữ tư liệu", "workspace", "Nhóm tài nguyên", ["view", "upload", "edit", "delete", "admin"]),
        ("storage", "Lưu trữ tư liệu", "trash", "Thùng rác", ["view", "edit", "delete"]),
        ("storage", "Lưu trữ tư liệu", "search", "Tìm kiếm & khai thác", ["view"]),
    ];

    public static IReadOnlyList<Definition> All { get; } = Build();

    private static List<Definition> Build()
    {
        var list = new List<Definition>();
        var order = 0;
        foreach (var f in Functions)
            foreach (var a in f.Actions)
            {
                var actionName = Actions.All.First(x => x.Code == a).Name;
                list.Add(new Definition($"{f.Function}.{a}", f.Module, f.ModuleName, f.Function, f.FunctionName, a, $"{f.FunctionName} - {actionName}", order++));
            }
        return list;
    }

    /// <summary>System role codes (not deletable).</summary>
    public static class Roles
    {
        public const string Admin = "quan_tri", Editor = "bien_tap", Approver = "phe_duyet", Viewer = "chi_xem";
    }

    public static IReadOnlyDictionary<string, string[]> DefaultRolePermissions { get; } = new Dictionary<string, string[]>
    {
        [Roles.Admin] = All.Select(p => p.Code).ToArray(),
        [Roles.Editor] = [DocView, DocUpload, DocEdit, DocDelete, DocShare, WorkspaceView, WorkspaceCreate, TrashView, TrashRestore, TrashPurge,
            SearchView, CatalogView, TicketCreate],
        [Roles.Approver] = [DocView, DocApprove, DocShare, WorkspaceView, TrashView, TrashRestore, SearchView, CatalogView, TicketCreate],
        [Roles.Viewer] = [DocView, WorkspaceView, SearchView, CatalogView, TicketCreate],
    };
}
