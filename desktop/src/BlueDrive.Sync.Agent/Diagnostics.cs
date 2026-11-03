using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BlueDrive.Sync.Core;

namespace BlueDrive.Sync.Agent;

/// <summary>
/// "Xuất gói chẩn đoán" (spec 8.3): logs, accounts (no secrets), sync state summary and system information in one zip for support.
/// Tokens, passwords and file contents are never included; anything that looks like a token in the logs is masked.
/// </summary>
public static partial class Diagnostics
{
    [GeneratedRegex(@"(?i)(bearer[ \t]+|access_token[=:"" \t]+|refresh_token[=:"" \t]+|device_secret[=:"" \t]+|password[=:"" \t]+)[^\s&""',;]+")]
    private static partial Regex Secret();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex Jwt();

    public static string Redact(string text) => Jwt().Replace(Secret().Replace(text, m => m.Groups[1].Value + "***"), "***");

    public static string Export(AccountStore store, string logsDir, string destination, string appVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (File.Exists(destination)) File.Delete(destination);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);

        Add(zip, "he-thong.txt", $"""
            BlueDrive Desktop {appVersion}
            Hệ điều hành: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})
            .NET: {RuntimeInformation.FrameworkDescription}
            Thời điểm xuất: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}
            Múi giờ: {TimeZoneInfo.Local.DisplayName}
            """);

        var sb = new StringBuilder();
        foreach (var acc in store.Load())
        {
            sb.AppendLine($"Tài khoản: {acc.Username} ({acc.DisplayName}) — {acc.ServerUrl}");
            sb.AppendLine($"  Thư mục: {acc.SyncFolder}; chế độ: {(acc.OnDemand ? "tệp theo yêu cầu" : "đồng bộ đầy đủ")}; tạm dừng: {acc.Paused}");
            sb.AppendLine($"  Đăng nhập: {(store.Tokens(acc.Id).Load() is null ? "cần đăng nhập lại" : "còn hiệu lực")}; thiết bị: {acc.DeviceId}");
            var dbPath = store.StateDbPath(acc.Id);
            if (!File.Exists(dbPath)) continue;
            try
            {
                using var db = new SyncStateDb(dbPath);
                foreach (var r in db.Roots())
                    sb.AppendLine($"  Gốc {r.RootId} → \"{r.LocalPath}\" bật={r.Enabled} cursor={r.Cursor} mục={db.Items(r.RootId).Count} chờ-xóa-hàng-loạt={r.PendingMassDelete}");
                foreach (var c in db.Conflicts()) sb.AppendLine($"  Xung đột [{c.Kind}] {c.Path} → {c.ConflictPath} ({c.CreatedAt:u})");
                sb.AppendLine("  Hoạt động gần đây:");
                foreach (var a in db.Activity(200)) sb.AppendLine($"    {a.At:u} {a.Action}: {a.Path} {a.Detail}");
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { sb.AppendLine($"  Không đọc được trạng thái: {ex.Message}"); }
        }
        Add(zip, "tai-khoan-va-trang-thai.txt", Redact(sb.ToString()));

        if (Directory.Exists(logsDir))
            foreach (var log in Directory.EnumerateFiles(logsDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
            {
                // Logs may be open for writing by the running app.
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                Add(zip, "logs/" + Path.GetFileName(log), Redact(reader.ReadToEnd()));
            }
        return destination;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
        w.Write(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }
}
