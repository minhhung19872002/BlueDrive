namespace BlueDrive.Application.Settings;

/// <summary>Keys of system_settings with their default values and Vietnamese labels (shown on the settings screen).</summary>
public static class SettingKeys
{
    public const string PasswordMinLength = "password.min_length";
    public const string PasswordRequireUpper = "password.require_upper";
    public const string PasswordRequireLower = "password.require_lower";
    public const string PasswordRequireDigit = "password.require_digit";
    public const string PasswordRequireSpecial = "password.require_special";
    public const string PasswordExpiryDays = "password.expiry_days";
    public const string PasswordHistory = "password.history_count";
    public const string LockoutThreshold = "lockout.threshold";
    public const string LockoutMinutes = "lockout.minutes";
    public const string LoginIpMaxFailures = "login.ip_max_failures";
    public const string LoginIpBlockMinutes = "login.ip_block_minutes";
    public const string SessionIdleMinutes = "session.idle_timeout_minutes";
    public const string QuotaPersonalDefaultGb = "quota.personal_default_gb";
    public const string QuotaGroupDefaultGb = "quota.group_default_gb";
    public const string QuotaWarningPercent = "quota.warning_percent";
    /// <summary>Spec 8.2: storage of the whole system (GB, 0 = no limit), above the Spaces and Drives.</summary>
    public const string QuotaTenantGb = "quota.tenant_gb";
    public const string TrashRetentionDays = "trash.retention_days";
    /// <summary>Spec 8.1: 93 days from the first deletion, both trash stages together.</summary>
    public const int DefaultTrashDays = 93;
    public const string UploadMaxSizeMb = "upload.max_size_mb";
    public const string UploadAllowedExtensions = "upload.allowed_extensions";
    public const string DownloadUnusualPerHour = "download.unusual_per_hour";
    public const string BackupCron = "backup.cron";
    public const string BackupRetentionCount = "backup.retention_count";
    public const string BackupFullDayOfWeek = "backup.full_day_of_week";
    public const string BackupToS3 = "backup.to_s3";
    public const string AlertEmailAdmins = "alert.email_admins";
    public const string BrandingAppName = "branding.app_name";
    public const string BrandingOrgName = "branding.org_name";
    public const string BrandingLogoUrl = "branding.logo_url";
    public const string ReportShowNationalHeader = "report.show_national_header";
    public const string ReportGoverningBody = "report.governing_body";
    public const string SyncEnabled = "sync.enabled";
    public const string SyncAllowedRoles = "sync.allowed_roles";
    public const string SyncAllowedOrganizations = "sync.allowed_organizations";
    public const string SyncMaxFileSizeMb = "sync.max_file_size_mb";
    public const string SyncBlockedExtensions = "sync.blocked_extensions";
    public const string SyncOnlineOnlyWorkspaces = "sync.online_only_workspaces";
    public const string SyncMinClientVersion = "sync.min_client_version";
    public const string SyncViewOnlyAsLink = "sync.view_only_as_link";
    public const string SyncDeviceRefreshDays = "sync.device_refresh_days";
    public const string SyncJournalRetentionDays = "sync.journal_retention_days";
    public const string SyncPollSeconds = "sync.poll_seconds";
    public const string ShareLinkAllowEdit = "share.link_allow_edit";
    public const string ShareExternal = "share.external";
    public const string ShareAnyoneLinkMaxDays = "share.anyone_link_max_days";
    public const string GuestEnabled = "guest.enabled";
    public const string GuestAllowedDomains = "guest.allowed_domains";
    public const string GuestBlockedDomains = "guest.blocked_domains";
    public const string GuestInactiveDays = "guest.inactive_days";

    public static readonly string[] DefaultAllowedExtensions =
    [
        "jpg", "jpeg", "png", "gif", "webp", "svg", "bmp", "tif", "tiff", "heic", "heif",
        "mp4", "mov", "avi", "mkv", "webm", "m4v",
        "mp3", "wav", "aac", "m4a", "ogg", "flac",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "odt", "ods", "odp", "rtf", "csv",
        // Macro-enabled Office files (like SharePoint): kept and scanned; the online editor only shows them, macros never run there.
        "docm", "xlsm", "pptm",
        "epub", "psd", "ai", "indd", "zip", "rar", "7z",
    ];

    public sealed record Definition(string Key, object Default, string Group, string Label, string Type);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(PasswordMinLength, 8, "Chính sách mật khẩu", "Độ dài tối thiểu", "number"),
        new(PasswordRequireUpper, true, "Chính sách mật khẩu", "Bắt buộc chữ hoa", "boolean"),
        new(PasswordRequireLower, true, "Chính sách mật khẩu", "Bắt buộc chữ thường", "boolean"),
        new(PasswordRequireDigit, true, "Chính sách mật khẩu", "Bắt buộc chữ số", "boolean"),
        new(PasswordRequireSpecial, true, "Chính sách mật khẩu", "Bắt buộc ký tự đặc biệt", "boolean"),
        new(PasswordExpiryDays, 90, "Chính sách mật khẩu", "Số ngày hết hạn mật khẩu (0 = không hết hạn)", "number"),
        new(PasswordHistory, 5, "Chính sách mật khẩu", "Không trùng N mật khẩu gần nhất", "number"),
        new(LockoutThreshold, 5, "Đăng nhập & phiên", "Số lần đăng nhập sai trước khi khóa tạm", "number"),
        new(LockoutMinutes, 15, "Đăng nhập & phiên", "Thời gian khóa tạm (phút)", "number"),
        new(LoginIpMaxFailures, 30, "Đăng nhập & phiên", "Số lần đăng nhập sai tối đa từ một địa chỉ IP (mọi tài khoản cộng lại, 0 = không giới hạn)", "number"),
        new(LoginIpBlockMinutes, 15, "Đăng nhập & phiên", "Khoảng thời gian đếm và thời gian chặn địa chỉ IP đăng nhập sai nhiều (phút)", "number"),
        new(SessionIdleMinutes, 30, "Đăng nhập & phiên", "Tự đăng xuất khi không hoạt động (phút)", "number"),
        new(QuotaPersonalDefaultGb, 10, "Dung lượng", "Dung lượng mặc định cá nhân (GB)", "number"),
        new(QuotaGroupDefaultGb, 50, "Dung lượng", "Dung lượng mặc định nhóm (GB)", "number"),
        new(QuotaWarningPercent, 80, "Dung lượng", "Ngưỡng cảnh báo dung lượng đầu tiên (%) — cảnh báo tiếp ở 90% và 100%", "number"),
        new(QuotaTenantGb, 0, "Dung lượng", "Dung lượng toàn hệ thống (GB, 0 = không giới hạn)", "number"),
        new(TrashRetentionDays, DefaultTrashDays, "Lưu trữ", "Số ngày giữ trong thùng rác (cả 2 tầng, tính từ lúc xóa)", "number"),
        new(UploadMaxSizeMb, 5120, "Lưu trữ", "Kích thước tải lên tối đa (MB)", "number"),
        new(UploadAllowedExtensions, DefaultAllowedExtensions, "Lưu trữ", "Định dạng tệp cho phép", "tags"),
        new(ShareExternal, "anyone", "Chia sẻ", "Chia sẻ ra ngoài (tầng hệ thống; Space, thư mục, tệp chỉ đặt chặt hơn)", "external"),
        new(ShareAnyoneLinkMaxDays, 30, "Chia sẻ", "Thời hạn tối đa của liên kết cho bất kỳ ai (ngày, 0 = không giới hạn)", "number"),
        new(GuestEnabled, true, "Khách bên ngoài", "Cho phép mời khách bên ngoài qua email (đăng nhập bằng mã gửi email)", "boolean"),
        new(GuestAllowedDomains, Array.Empty<string>(), "Khách bên ngoài", "Chỉ mời được email thuộc các tên miền (để trống = mọi tên miền)", "tags"),
        new(GuestBlockedDomains, Array.Empty<string>(), "Khách bên ngoài", "Không mời email thuộc các tên miền", "tags"),
        new(GuestInactiveDays, 90, "Khách bên ngoài", "Tài khoản khách tự hết hạn sau số ngày không dùng", "number"),
        new(ShareLinkAllowEdit, true, "Chia sẻ", "Cho phép liên kết chia sẻ cho người không đăng nhập chỉnh sửa tài liệu (Word, Excel, PowerPoint)", "boolean"),
        new(DownloadUnusualPerHour, 200, "Giám sát", "Ngưỡng tải xuống bất thường (tệp/giờ)", "number"),
        new(AlertEmailAdmins, true, "Giám sát", "Gửi email cảnh báo cho quản trị viên", "boolean"),
        new(BackupCron, "0 2 * * *", "Sao lưu", "Lịch sao lưu tự động (cron)", "cron"),
        new(BackupRetentionCount, 7, "Sao lưu", "Số bản sao lưu giữ lại", "number"),
        new(BackupFullDayOfWeek, 0, "Sao lưu", "Ngày sao lưu đầy đủ trong tuần (0 = Chủ nhật)", "number"),
        new(BackupToS3, false, "Sao lưu", "Sao chép bản sao lưu lên S3", "boolean"),
        new(BrandingAppName, "BlueDrive", "Giao diện", "Tên phần mềm", "text"),
        new(BrandingOrgName, "Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số", "Giao diện", "Tên đơn vị", "text"),
        new(BrandingLogoUrl, "", "Giao diện", "Đường dẫn logo", "text"),
        new(ReportShowNationalHeader, true, "Báo cáo", "Hiển thị Quốc hiệu trên bản in", "boolean"),
        new(ReportGoverningBody, "CỤC DÂN SỐ - BỘ Y TẾ", "Báo cáo", "Cơ quan chủ quản (dòng trên tiêu đề bản in)", "text"),
        new(SyncEnabled, true, "Đồng bộ máy tính", "Cho phép đồng bộ bằng BlueDrive Desktop", "boolean"),
        new(SyncAllowedRoles, Array.Empty<string>(), "Đồng bộ máy tính", "Chỉ cho phép các vai trò (mã vai trò; để trống = tất cả)", "tags"),
        new(SyncAllowedOrganizations, Array.Empty<string>(), "Đồng bộ máy tính", "Chỉ cho phép các đơn vị (mã đơn vị, gồm đơn vị con; để trống = tất cả)", "tags"),
        new(SyncMaxFileSizeMb, 0, "Đồng bộ máy tính", "Kích thước tệp tối đa được đồng bộ (MB, 0 = theo giới hạn tải lên)", "number"),
        new(SyncBlockedExtensions, Array.Empty<string>(), "Đồng bộ máy tính", "Đuôi tệp không đồng bộ xuống máy", "tags"),
        new(SyncOnlineOnlyWorkspaces, Array.Empty<string>(), "Đồng bộ máy tính", "Nhóm tài nguyên bắt buộc chỉ trực tuyến (tên nhóm; không cho \"Luôn giữ trên thiết bị\")", "tags"),
        new(SyncMinClientVersion, "1.0.0", "Đồng bộ máy tính", "Phiên bản ứng dụng tối thiểu", "text"),
        new(SyncViewOnlyAsLink, true, "Đồng bộ máy tính", "Tư liệu chỉ có quyền Xem hiện dạng liên kết \"Mở trên web\" (tắt = ẩn)", "boolean"),
        new(SyncDeviceRefreshDays, 90, "Đồng bộ máy tính", "Thời hạn đăng nhập của thiết bị (ngày)", "number"),
        new(SyncJournalRetentionDays, 90, "Đồng bộ máy tính", "Giữ nhật ký thay đổi đồng bộ (ngày)", "number"),
        new(SyncPollSeconds, 30, "Đồng bộ máy tính", "Chu kỳ hỏi thay đổi khi không có kết nối thời gian thực (giây)", "number"),
    ];

    public static Definition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}
