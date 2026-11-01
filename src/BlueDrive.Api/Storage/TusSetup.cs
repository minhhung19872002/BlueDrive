using System.Security.Claims;
using System.Text;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Infrastructure.Storage;
using MediatR;
using Microsoft.Extensions.Options;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

namespace BlueDrive.Api.Storage;

/// <summary>
/// Resumable uploads (tus 1.0) at /tus. The tus layer only receives bytes; the client then calls
/// POST /api/v1/uploads/{id}/complete which validates content and creates the file (clear JSON errors).
/// </summary>
public static class TusSetup
{
    public static string TusDirectory(IServiceProvider sp) => Path.Combine(sp.GetRequiredService<IOptions<StorageOptions>>().Value.LocalRoot, ".tus");

    public static UploadMetadata ParseMetadata(Dictionary<string, Metadata> meta)
    {
        string? S(string key) => meta.TryGetValue(key, out var v) ? v.GetString(Encoding.UTF8) : null;
        Guid? G(string key) => Guid.TryParse(S(key), out var g) ? g : null;
        return new UploadMetadata(
            S("filename") ?? S("name") ?? "Không tên", G("workspaceId") ?? Guid.Empty, G("folderId"), S("relativePath"), G("targetFileId"),
            S("title"), S("description"), S("tags")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            S("catalogItemIds")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList(),
            S("changeNote"));
    }

    public static void MapBlueDriveTus(this WebApplication app)
    {
        app.MapTus("/tus", ctx =>
        {
            var dir = TusDirectory(ctx.RequestServices);
            Directory.CreateDirectory(dir);
            return Task.FromResult(new DefaultTusConfiguration
            {
                Store = new TusDiskStore(dir),
                MaxAllowedUploadSizeInBytesLong = 50L * 1024 * 1024 * 1024,
                Expiration = new SlidingExpiration(TimeSpan.FromHours(24)),
                Events = new Events
                {
                    OnAuthorizeAsync = c =>
                    {
                        if (c.HttpContext.User.Identity?.IsAuthenticated != true) c.FailRequest(System.Net.HttpStatusCode.Unauthorized, "Phiên đăng nhập không hợp lệ.");
                        else if (c.HttpContext.User.FindFirstValue("pwd_change") == "1") c.FailRequest(System.Net.HttpStatusCode.Forbidden, "Bạn cần đổi mật khẩu trước khi tải lên.");
                        return Task.CompletedTask;
                    },
                    OnBeforeCreateAsync = async c =>
                    {
                        var meta = ParseMetadata(c.Metadata);
                        if (meta.WorkspaceId == Guid.Empty && meta.TargetFileId is null)
                        {
                            c.FailRequest(System.Net.HttpStatusCode.BadRequest, "Thiếu thông tin nơi lưu (workspaceId).");
                            return;
                        }
                        try
                        {
                            await c.HttpContext.RequestServices.GetRequiredService<ISender>().Send(new ValidateUploadCommand(meta, c.UploadLength), c.CancellationToken);
                        }
                        catch (AppException ex)
                        {
                            // tus clients surface the response body to the user.
                            c.FailRequest((System.Net.HttpStatusCode)(ex.StatusCode == 413 ? 413 : ex.StatusCode), ex.Message);
                        }
                    },
                    OnCreateCompleteAsync = async c =>
                    {
                        var uid = c.HttpContext.User.FindFirstValue("sub");
                        await File.WriteAllTextAsync(Path.Combine(dir, c.FileId + ".owner"), uid, c.CancellationToken);
                    },
                },
            });
        }).RequireAuthorization();
    }
}

/// <summary>Removes abandoned partial uploads hourly.</summary>
public sealed class TusCleanupService(IServiceProvider sp, ILogger<TusCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var dir = TusSetup.TusDirectory(sp);
                if (!Directory.Exists(dir)) continue;
                var removed = await new TusDiskStore(dir).RemoveExpiredFilesAsync(stoppingToken);
                foreach (var owner in Directory.EnumerateFiles(dir, "*.owner"))
                    if (!File.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(owner)))) File.Delete(owner);
                if (removed > 0) logger.LogInformation("Đã dọn {Count} phiên tải lên dang dở quá hạn", removed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Lỗi dọn phiên tải lên"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
