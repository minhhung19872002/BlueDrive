using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Users;

// Spec 8.3: a user leaving (or moving to another unit) — what they own goes to someone, what they shared keeps working.

public sealed record OffboardingSpaceDto(Guid Id, string Name, string Role, bool SoleOwner);

public sealed record OffboardingPreviewDto(Guid UserId, string FullName, string Username, bool IsActive, int Files, int Folders, long Bytes, int ActiveLinks,
    IReadOnlyList<string> Groups, IReadOnlyList<OffboardingSpaceDto> Spaces);

public sealed record GetOffboardingPreviewQuery(Guid UserId) : IRequest<OffboardingPreviewDto>;

/// <param name="RecipientId">Who gets the Drive ("Dữ liệu từ …" in their Drive) and the Spaces the user owns alone.</param>
/// <param name="RevokeLinks">Revoke the sharing links the user created (otherwise they keep working).</param>
public sealed record OffboardUserCommand(Guid UserId, Guid RecipientId, bool RevokeLinks, bool Deactivate = true) : IRequest<OffboardingReportDto>;

public sealed record OffboardingLine(string Item, string Detail);

public sealed record OffboardingReportDto(Guid Id, Guid UserId, string UserName, Guid RecipientId, string RecipientName, DateTime PerformedAt, string PerformedByName,
    IReadOnlyList<OffboardingLine> Lines);

public sealed record ListOffboardingsQuery : IRequest<IReadOnlyList<OffboardingReportDto>>;

public sealed record ExportOffboardingQuery(Guid Id, string Format) : IRequest<ExportedFile>;

/// <summary>Spec 8.3 "chuyển đơn vị": the user's unit changes, the report says which accesses come or go with it.</summary>
public sealed record ChangeUserUnitCommand(Guid UserId, Guid? OrganizationId) : IRequest<UnitChangeReportDto>;

public sealed record UnitChangeReportDto(string FromUnit, string ToUnit, IReadOnlyList<string> Lost, IReadOnlyList<string> Gained);

public sealed class OffboardingHandlers(IAppDbContext db, ICurrentUser me, IPermissionService perms, IAuditLogger audit, TimeProvider clock, ISettingsService settings,
    MoveItemsHandler mover, LinkModel links, SessionRevoker sessions, PermissionProjection projection, ITableExporter exporter, IItemPermissionService engine)
    : IRequestHandler<GetOffboardingPreviewQuery, OffboardingPreviewDto>, IRequestHandler<OffboardUserCommand, OffboardingReportDto>,
      IRequestHandler<ListOffboardingsQuery, IReadOnlyList<OffboardingReportDto>>, IRequestHandler<ExportOffboardingQuery, ExportedFile>,
      IRequestHandler<ChangeUserUnitCommand, UnitChangeReportDto>
{
    private async Task<Guid> RequireAsync(CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.UserAdmin, ct)) throw new ForbiddenException("Bạn không có quyền xử lý người dùng nghỉ việc.");
        return uid;
    }

    private async Task<List<OffboardingSpaceDto>> SpacesAsync(Guid userId, CancellationToken ct)
    {
        var owned = await db.Workspaces.AsNoTracking().Where(w => w.OwnerId == userId && w.Type != WorkspaceType.Personal && w.Status != SpaceStatus.Deleted)
            .Select(w => new { w.Id, w.Name }).ToListAsync(ct);
        var memberOf = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Workspace!.Type != WorkspaceType.Personal && m.Workspace.Status != SpaceStatus.Deleted)
            .Select(m => new { m.WorkspaceId, m.Workspace!.Name, m.MemberRole }).ToListAsync(ct);
        var otherOwners = (await db.WorkspaceMembers.AsNoTracking()
                .Where(m => m.UserId != userId && m.MemberRole == WorkspaceMemberRole.Owner && owned.Select(o => o.Id).Contains(m.WorkspaceId))
                .Select(m => m.WorkspaceId).ToListAsync(ct)).ToHashSet();
        var list = owned.Select(o => new OffboardingSpaceDto(o.Id, o.Name, "Chủ sở hữu", !otherOwners.Contains(o.Id))).ToList();
        list.AddRange(memberOf.Where(m => owned.All(o => o.Id != m.WorkspaceId))
            .Select(m => new OffboardingSpaceDto(m.WorkspaceId, m.Name, m.MemberRole switch
            {
                WorkspaceMemberRole.Owner => "Chủ sở hữu",
                WorkspaceMemberRole.Manager => "Quản lý",
                WorkspaceMemberRole.Editor => "Chỉnh sửa",
                _ => "Thành viên",
            }, false)));
        return list;
    }

    public async Task<OffboardingPreviewDto> Handle(GetOffboardingPreviewQuery q, CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == q.UserId, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        var drive = await db.Workspaces.AsNoTracking().Where(w => w.OwnerId == user.Id && w.Type == WorkspaceType.Personal).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        var files = drive is null ? 0 : await db.Files.CountAsync(f => f.WorkspaceId == drive && !f.IsDeleted, ct);
        var folders = drive is null ? 0 : await db.Folders.CountAsync(f => f.WorkspaceId == drive && !f.IsDeleted, ct);
        var bytes = drive is null ? 0 : await db.Workspaces.Where(w => w.Id == drive).Select(w => w.UsedBytes).FirstAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var activeLinks = await db.ShareLinks.CountAsync(l => l.CreatedBy == user.Id && !l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now), ct);
        var groups = await db.UserGroupMembers.AsNoTracking().Where(m => m.UserId == user.Id).Select(m => m.Group!.Name).ToListAsync(ct);
        return new OffboardingPreviewDto(user.Id, user.FullName, user.Username, user.IsActive, files, folders, bytes, activeLinks, groups, await SpacesAsync(user.Id, ct));
    }

    public async Task<OffboardingReportDto> Handle(OffboardUserCommand cmd, CancellationToken ct)
    {
        var adminId = await RequireAsync(ct);
        if (cmd.UserId == cmd.RecipientId) throw new AppException("Người nhận phải là người khác.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        if (user.Username == "admin") throw new AppException("Không xử lý nghỉ việc cho tài khoản quản trị mặc định.");
        var recipient = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.RecipientId && u.IsActive && !u.IsGuest, ct)
                        ?? throw new AppException("Người nhận phải là người dùng nội bộ đang hoạt động.");
        var now = clock.GetUtcNow().UtcDateTime;
        var lines = new List<OffboardingLine>();

        // 1. The Drive goes into "Dữ liệu từ <tên>" in the recipient's Drive, every share kept (the move keeps own permissions).
        var source = await db.Workspaces.FirstOrDefaultAsync(w => w.OwnerId == user.Id && w.Type == WorkspaceType.Personal, ct);
        var target = await WorkspaceProvisioning.EnsurePersonalWorkspaceAsync(db, settings, recipient, ct);
        await db.SaveChangesAsync(ct);
        if (source is not null)
        {
            // Two queries: EF cannot translate Concat after projecting into a record constructor.
            var top = (await db.Folders.Where(f => f.WorkspaceId == source.Id && f.ParentId == null && !f.IsDeleted).Select(f => f.Id).ToListAsync(ct)).Select(id => new ItemRef("folder", id))
                .Concat((await db.Files.Where(f => f.WorkspaceId == source.Id && f.FolderId == null && !f.IsDeleted).Select(f => f.Id).ToListAsync(ct)).Select(id => new ItemRef("file", id))).ToList();
            var files = await db.Files.CountAsync(f => f.WorkspaceId == source.Id && !f.IsDeleted, ct);
            var folder = new Folder { WorkspaceId = target.Id, Name = await StorageNames.UniqueFolderNameAsync(db, target.Id, null, $"Dữ liệu từ {user.FullName}", null, ct) };
            folder.Path = StorageNames.FolderPath(null, folder.Id);
            db.Folders.Add(folder);
            await db.SaveChangesAsync(ct);
            if (top.Count > 0) await mover.MoveAsync(adminId, new MoveItemsCommand(top, target.Id, folder.Id), trusted: true, ct);
            lines.Add(new("Drive của tôi", $"{files} tệp chuyển vào thư mục \"{folder.Name}\" trong Drive của {recipient.FullName}; quyền chia sẻ giữ nguyên."));
        }

        // 2. The links the user made: kept or revoked.
        var userLinks = await db.ShareLinks.Where(l => l.CreatedBy == user.Id && !l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now)).ToListAsync(ct);
        if (cmd.RevokeLinks)
        {
            foreach (var l in userLinks)
            {
                l.IsRevoked = true;
                await links.RevokeAsync(l, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        lines.Add(new("Liên kết chia sẻ", userLinks.Count == 0 ? "Không có liên kết đang hiệu lực."
            : cmd.RevokeLinks ? $"Đã thu hồi {userLinks.Count} liên kết." : $"Giữ {userLinks.Count} liên kết (vẫn hoạt động)."));

        // 3. Out of every group and Space; Spaces they own alone go to the recipient.
        var spaces = await SpacesAsync(user.Id, ct);
        foreach (var s in spaces.Where(s => s.Role == "Chủ sở hữu"))
        {
            var ws = await db.Workspaces.FirstAsync(w => w.Id == s.Id, ct);
            if (ws.OwnerId == user.Id)
            {
                ws.OwnerId = recipient.Id;
                if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == ws.Id && m.UserId == recipient.Id, ct))
                    db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = recipient.Id, MemberRole = WorkspaceMemberRole.Owner, AddedAt = now });
                else await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id && m.UserId == recipient.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(m => m.MemberRole, WorkspaceMemberRole.Owner), ct);
                lines.Add(new($"Space \"{s.Name}\"", s.SoleOwner ? $"Là chủ sở hữu duy nhất — chuyển cho {recipient.FullName}." : $"Chủ sở hữu chuyển cho {recipient.FullName}."));
            }
        }
        await db.SaveChangesAsync(ct);
        var groups = await db.UserGroupMembers.Where(m => m.UserId == user.Id).Select(m => m.Group!.Name).ToListAsync(ct);
        await db.UserGroupMembers.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(ct);
        var memberSpaces = await db.WorkspaceMembers.Where(m => m.UserId == user.Id && m.Workspace!.Type != WorkspaceType.Personal).Select(m => m.WorkspaceId).ToListAsync(ct);
        await db.WorkspaceMembers.Where(m => m.UserId == user.Id && m.Workspace!.Type != WorkspaceType.Personal).ExecuteDeleteAsync(ct);
        lines.Add(new("Nhóm người dùng", groups.Count == 0 ? "Không thuộc nhóm nào." : $"Gỡ khỏi {groups.Count} nhóm: {string.Join(", ", groups)}."));
        lines.Add(new("Space", spaces.Count == 0 ? "Không là thành viên Space nào." : $"Gỡ khỏi {spaces.Count} Space: {string.Join(", ", spaces.Select(s => s.Name))}."));

        // 4. Deactivated, signed out everywhere.
        if (cmd.Deactivate && user.IsActive)
        {
            user.IsActive = false;
            await db.SaveChangesAsync(ct);
            await sessions.RevokeAllForUserAsync(user.Id, null, ct);
            lines.Add(new("Tài khoản", "Đã vô hiệu hóa và đăng xuất khỏi mọi thiết bị."));
        }
        await projection.ProjectAsync(memberSpaces.Concat(spaces.Select(s => s.Id)).Append(target.Id).Concat(source is null ? [] : [source.Id]).Distinct().ToList(),
            [user.Id, recipient.Id], ct);

        var record = new OffboardingRecord
        {
            UserId = user.Id, RecipientId = recipient.Id, PerformedBy = adminId, PerformedAt = now, RevokedLinks = cmd.RevokeLinks,
            ReportJson = JsonSerializer.Serialize(lines),
        };
        db.OffboardingRecords.Add(record);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.OwnershipTransferred, ResourceName: $"Người dùng nghỉ việc {user.Username}",
            Detail: new { offboarding = record.Id, user = user.Id, recipient = recipient.Id, cmd.RevokeLinks, lines }), ct);
        return await ToDtoAsync(record, ct);
    }

    private async Task<OffboardingReportDto> ToDtoAsync(OffboardingRecord r, CancellationToken ct)
    {
        var ids = new[] { r.UserId, r.RecipientId, r.PerformedBy };
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => $"{u.FullName} ({u.Username})", ct);
        return new OffboardingReportDto(r.Id, r.UserId, names.GetValueOrDefault(r.UserId, ""), r.RecipientId, names.GetValueOrDefault(r.RecipientId, ""), r.PerformedAt,
            names.GetValueOrDefault(r.PerformedBy, ""), JsonSerializer.Deserialize<List<OffboardingLine>>(r.ReportJson) ?? []);
    }

    public async Task<IReadOnlyList<OffboardingReportDto>> Handle(ListOffboardingsQuery q, CancellationToken ct)
    {
        await RequireAsync(ct);
        var list = new List<OffboardingReportDto>();
        foreach (var r in await db.OffboardingRecords.AsNoTracking().OrderByDescending(r => r.PerformedAt).Take(200).ToListAsync(ct)) list.Add(await ToDtoAsync(r, ct));
        return list;
    }

    public async Task<ExportedFile> Handle(ExportOffboardingQuery q, CancellationToken ct)
    {
        await RequireAsync(ct);
        var r = await db.OffboardingRecords.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw new NotFoundException("Không tìm thấy biên bản bàn giao.");
        var dto = await ToDtoAsync(r, ct);
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "Người nghỉ việc", dto.UserName },
            new[] { "Người nhận bàn giao", dto.RecipientName },
            new[] { "Thời điểm", dto.PerformedAt.AddHours(7).ToString("dd/MM/yyyy HH:mm") },
            new[] { "Người thực hiện", dto.PerformedByName },
        };
        rows.AddRange(dto.Lines.Select(l => (IReadOnlyList<string?>)new[] { l.Item, l.Detail }));
        return await exporter.ExportAsync(new TableExport("Biên bản bàn giao dữ liệu người dùng nghỉ việc", [new("Hạng mục", 1), new("Chi tiết", 3)], rows, null, dto.PerformedByName),
            ExportFormat.Normalize(q.Format), ct);
    }

    public async Task<UnitChangeReportDto> Handle(ChangeUserUnitCommand cmd, CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        if (cmd.OrganizationId is { } org && !await db.Organizations.AnyAsync(o => o.Id == org, ct)) throw new NotFoundException("Không tìm thấy đơn vị.");
        var names = await db.Organizations.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name, ct);
        var from = user.OrganizationId is { } f ? names.GetValueOrDefault(f, "") : "(không có)";
        var before = await AccessibleAsync(user.Id, ct);
        user.OrganizationId = cmd.OrganizationId;
        await db.SaveChangesAsync(ct);
        await projection.ProjectAsync([], [user.Id], ct);
        var after = await AccessibleAsync(user.Id, ct);
        var to = cmd.OrganizationId is { } t ? names.GetValueOrDefault(t, "") : "(không có)";
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceName: $"Người dùng {user.Username}", Detail: new { entity = "user", unitChanged = new { from, to } }), ct);
        return new UnitChangeReportDto(from, to, before.Except(after).Order().ToList(), after.Except(before).Order().ToList());
    }

    /// <summary>Spaces, folders and files a user reaches through their memberships (units, groups) — what a unit change can alter.</summary>
    private async Task<HashSet<string>> AccessibleAsync(Guid userId, CancellationToken ct)
    {
        var principals = db.PrincipalMemberships.Where(m => m.UserId == userId && m.PrincipalId != userId).Select(m => m.PrincipalId);
        var scopes = await db.AclEntries.AsNoTracking().Where(e => principals.Contains(e.PrincipalId)).Select(e => e.ScopeId).Distinct().ToListAsync(ct);
        var nodes = await db.Securables.AsNoTracking().Where(s => scopes.Contains(s.Id) && !s.IsDeleted).Select(s => new { s.Id, s.Kind, s.Name }).ToListAsync(ct);
        var result = new HashSet<string>();
        foreach (var n in nodes)
            if (await engine.GetEffectiveAsync(userId, n.Id, ct) != Domain.Permissions.AccessRight.None)
                result.Add($"{(n.Kind is SecurableKind.Space ? "Space" : n.Kind is SecurableKind.Folder ? "Thư mục" : "Tệp")} \"{n.Name}\"");
        return result;
    }
}
