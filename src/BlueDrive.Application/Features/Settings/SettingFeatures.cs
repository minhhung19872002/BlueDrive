using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Settings;

public sealed record SettingDto(string Key, string Group, string Label, string Type, JsonElement Value, JsonElement Default);

public sealed record ListSettingsQuery : IRequest<IReadOnlyList<SettingDto>>;

public sealed class ListSettingsHandler(ISettingsService settings) : IRequestHandler<ListSettingsQuery, IReadOnlyList<SettingDto>>
{
    public async Task<IReadOnlyList<SettingDto>> Handle(ListSettingsQuery q, CancellationToken ct)
    {
        var stored = await settings.GetAllAsync(ct);
        return SettingKeys.All.Select(d =>
        {
            var def = JsonSerializer.SerializeToElement(d.Default);
            var val = stored.TryGetValue(d.Key, out var json) ? JsonDocument.Parse(json).RootElement.Clone() : def;
            return new SettingDto(d.Key, d.Group, d.Label, d.Type, val, def);
        }).ToList();
    }
}

/// <summary>Public, non-sensitive branding values (app name, organization, logo) for the login screen and header.</summary>
public sealed record GetBrandingQuery : IRequest<Dictionary<string, string>>;

public sealed class GetBrandingHandler(ISettingsService settings) : IRequestHandler<GetBrandingQuery, Dictionary<string, string>>
{
    public async Task<Dictionary<string, string>> Handle(GetBrandingQuery q, CancellationToken ct) => new()
    {
        ["appName"] = await settings.GetAsync(SettingKeys.BrandingAppName, "BlueDrive", ct),
        ["orgName"] = await settings.GetAsync(SettingKeys.BrandingOrgName, "", ct),
        ["logoUrl"] = await settings.GetAsync(SettingKeys.BrandingLogoUrl, "", ct),
        ["governingBody"] = await settings.GetAsync(SettingKeys.ReportGoverningBody, "CỤC DÂN SỐ - BỘ Y TẾ", ct),
        ["showNationalHeader"] = (await settings.GetAsync(SettingKeys.ReportShowNationalHeader, true, ct)) ? "true" : "false",
        ["sessionIdleMinutes"] = (await settings.GetAsync(SettingKeys.SessionIdleMinutes, 30, ct)).ToString(),
    };
}

public sealed record SettingChange(string Key, string Label, string From, string To);

public sealed record UpdateSettingsCommand(Dictionary<string, JsonElement> Values) : IRequest<int>;

public sealed class UpdateSettingsHandler(IAppDbContext db, ISettingsService settings, ICurrentUser me, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<UpdateSettingsCommand, int>
{
    public async Task<int> Handle(UpdateSettingsCommand cmd, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var current = await settings.GetAllAsync(ct);
        var changes = new List<SettingChange>();
        foreach (var (key, value) in cmd.Values)
        {
            var def = SettingKeys.Find(key);
            if (def is null) { errors[key] = ["Khóa cấu hình không tồn tại."]; continue; }
            var error = Validate(def, value);
            if (error is not null) { errors[key] = [error]; continue; }
            var json = value.GetRawText();
            var old = current.GetValueOrDefault(key) ?? JsonSerializer.Serialize(def.Default);
            if (old == json) continue;
            await settings.SetAsync(key, value, ct);
            changes.Add(new SettingChange(key, def.Label, old, json));
        }
        if (errors.Count > 0) throw new RequestValidationException(errors);
        if (changes.Count == 0) return 0;
        db.ChangeLogs.Add(new ChangeLog
        {
            Version = typeof(UpdateSettingsHandler).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            ChangeType = "config",
            Description = "Thay đổi cấu hình: " + string.Join("; ", changes.Select(c => c.Label)),
            AppliedBy = me.UserId,
            AppliedAt = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: "Cấu hình hệ thống", Detail: new { changes }), ct);
        return changes.Count;
    }

    private static string? Validate(SettingKeys.Definition def, JsonElement v) => def.Type switch
    {
        "number" when v.ValueKind != JsonValueKind.Number || v.GetDouble() < 0 => "Giá trị phải là số không âm.",
        "boolean" when v.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => "Giá trị phải là Có/Không.",
        "tags" when v.ValueKind != JsonValueKind.Array => "Giá trị phải là danh sách.",
        "cron" when v.ValueKind != JsonValueKind.String || !CronUtil.IsValid(v.GetString()) => "Biểu thức lịch (cron) không hợp lệ, ví dụ: 0 2 * * * (2 giờ sáng hằng ngày).",
        "text" when v.ValueKind != JsonValueKind.String => "Giá trị phải là chuỗi.",
        "external" when v.ValueKind != JsonValueKind.String || v.GetString() is not ("off" or "existing_guests" or "new_and_existing_guests" or "anyone")
            => "Mức chia sẻ ra ngoài không hợp lệ.",
        _ => null,
    };
}

public static class CronUtil
{
    public static bool IsValid(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        var parts = expr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts.All(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^[\d*/,\-]+$"));
    }
}

// ---------- Notifications ----------
public sealed record NotificationDto(Guid Id, string Type, string Title, string? Body, string? Link, bool IsRead, DateTime CreatedAt);

public sealed record ListNotificationsQuery(bool UnreadOnly = false) : PageQuery, IRequest<PagedResult<NotificationDto>>;

public sealed class ListNotificationsHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<ListNotificationsQuery, PagedResult<NotificationDto>>
{
    public Task<PagedResult<NotificationDto>> Handle(ListNotificationsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        return db.Notifications.AsNoTracking().Where(n => n.UserId == uid && (!q.UnreadOnly || !n.IsRead)).OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.Link, n.IsRead, n.CreatedAt)).ToPagedAsync(q, ct);
    }
}

public sealed record UnreadNotificationCountQuery : IRequest<int>;

public sealed class UnreadNotificationCountHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<UnreadNotificationCountQuery, int>
{
    public Task<int> Handle(UnreadNotificationCountQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        return db.Notifications.CountAsync(n => n.UserId == uid && !n.IsRead, ct);
    }
}

public sealed record MarkNotificationsReadCommand(IReadOnlyList<Guid>? Ids) : IRequest<int>;

public sealed class MarkNotificationsReadHandler(IAppDbContext db, ICurrentUser me) : IRequestHandler<MarkNotificationsReadCommand, int>
{
    public Task<int> Handle(MarkNotificationsReadCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var q = db.Notifications.Where(n => n.UserId == uid && !n.IsRead);
        if (cmd.Ids is { Count: > 0 }) q = q.Where(n => cmd.Ids.Contains(n.Id));
        return q.ExecuteUpdateAsync(u => u.SetProperty(n => n.IsRead, true), ct);
    }
}

public sealed record UpdateProfileCommand(string FullName, string? Email, string? Phone, string? Position) : IRequest<Unit>;

public sealed class UpdateProfileHandler(IAppDbContext db, ICurrentUser me, ISensitiveDataProtector pii, IAuditLogger audit) : IRequestHandler<UpdateProfileCommand, Unit>
{
    public async Task<Unit> Handle(UpdateProfileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.FullName)) throw new RequestValidationException(new Dictionary<string, string[]> { ["fullName"] = ["Vui lòng nhập họ và tên."] });
        if (!string.IsNullOrWhiteSpace(cmd.Email) && !System.Net.Mail.MailAddress.TryCreate(cmd.Email, out _))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["email"] = ["Email không đúng định dạng."] });
        var user = await db.Users.FirstAsync(u => u.Id == me.RequireUserId(), ct);
        var hash = pii.Hash(cmd.Email);
        if (hash is not null && await db.Users.AnyAsync(u => u.EmailHash == hash && u.Id != user.Id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["email"] = ["Email đã được sử dụng cho tài khoản khác."] });
        user.FullName = cmd.FullName.Trim();
        user.Email = pii.Protect(cmd.Email);
        user.EmailHash = hash;
        user.Phone = pii.Protect(cmd.Phone);
        user.Position = cmd.Position?.Trim();
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceName: $"Hồ sơ cá nhân {user.Username}", Detail: new { entity = "profile" }), ct);
        return Unit.Value;
    }
}
