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
    public const string SessionIdleMinutes = "session.idle_timeout_minutes";
    public const string QuotaPersonalDefaultGb = "quota.personal_default_gb";
    public const string QuotaGroupDefaultGb = "quota.group_default_gb";
    public const string QuotaWarningPercent = "quota.warning_percent";
    public const string TrashRetentionDays = "trash.retention_days";
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

    public static readonly string[] DefaultAllowedExtensions =
    [
        "jpg", "jpeg", "png", "gif", "webp", "svg", "bmp", "tif", "tiff", "heic", "heif",
        "mp4", "mov", "avi", "mkv", "webm", "m4v",
        "mp3", "wav", "aac", "m4a", "ogg", "flac",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "odt", "ods", "odp", "rtf", "csv",
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
        new(SessionIdleMinutes, 30, "Đăng nhập & phiên", "Tự đăng xuất khi không hoạt động (phút)", "number"),
        new(QuotaPersonalDefaultGb, 10, "Dung lượng", "Dung lượng mặc định cá nhân (GB)", "number"),
        new(QuotaGroupDefaultGb, 50, "Dung lượng", "Dung lượng mặc định nhóm (GB)", "number"),
        new(QuotaWarningPercent, 90, "Dung lượng", "Ngưỡng cảnh báo dung lượng (%)", "number"),
        new(TrashRetentionDays, 30, "Lưu trữ", "Số ngày giữ trong thùng rác", "number"),
        new(UploadMaxSizeMb, 5120, "Lưu trữ", "Kích thước tải lên tối đa (MB)", "number"),
        new(UploadAllowedExtensions, DefaultAllowedExtensions, "Lưu trữ", "Định dạng tệp cho phép", "tags"),
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
    ];

    public static Definition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}
