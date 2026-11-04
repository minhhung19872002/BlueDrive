using System.Text.RegularExpressions;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Comments;

public sealed record CommentAttachmentDto(Guid FileId, string Name, long SizeBytes, MediaType MediaType);

public sealed record CommentDto(Guid Id, Guid FileId, Guid? ParentId, string Content, IReadOnlyList<Guid> Mentions, IReadOnlyList<CommentAttachmentDto> Attachments,
    Guid? AuthorId, string AuthorName, DateTime CreatedAt, DateTime? UpdatedAt, bool IsEdited, bool IsDeleted, bool CanEdit);

public static class CommentMapping
{
    public static async Task<List<CommentDto>> MapAsync(IAppDbContext db, IReadOnlyList<Comment> comments, Guid viewerId, CancellationToken ct)
    {
        var authorIds = comments.Where(c => c.CreatedBy.HasValue).Select(c => c.CreatedBy!.Value).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var attIds = comments.SelectMany(c => c.AttachmentFileIds).Distinct().ToList();
        var atts = await db.Files.AsNoTracking().Where(f => attIds.Contains(f.Id) && !f.IsDeleted).ToDictionaryAsync(f => f.Id, f => new CommentAttachmentDto(f.Id, f.Name, f.SizeBytes, f.MediaType), ct);
        return comments.Select(c => new CommentDto(c.Id, c.FileId, c.ParentId, c.IsDeleted ? "" : c.Content, c.Mentions,
            c.IsDeleted ? [] : c.AttachmentFileIds.Where(atts.ContainsKey).Select(id => atts[id]).ToList(),
            c.CreatedBy, c.CreatedBy is { } a ? authors.GetValueOrDefault(a, "") : "", c.CreatedAt, c.UpdatedAt, c.IsEdited, c.IsDeleted,
            !c.IsDeleted && c.CreatedBy == viewerId)).ToList();
    }
}

/// <summary>#27/#38: discussion tab of a file — only users who can view the file see it.</summary>
public sealed record ListCommentsQuery(Guid FileId) : IRequest<IReadOnlyList<CommentDto>>;

public sealed class ListCommentsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<ListCommentsQuery, IReadOnlyList<CommentDto>>
{
    public async Task<IReadOnlyList<CommentDto>> Handle(ListCommentsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, ResourceType.File, q.FileId, AccessLevel.View, ct);
        var rows = await db.Comments.AsNoTracking().Where(c => c.FileId == q.FileId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return await CommentMapping.MapAsync(db, rows, uid, ct);
    }
}

public sealed record AddCommentCommand(Guid FileId, Guid? ParentId, string Content, IReadOnlyList<Guid>? Mentions, IReadOnlyList<Guid>? AttachmentFileIds) : IRequest<CommentDto>;

public sealed class AddCommentValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentValidator()
    {
        RuleFor(x => x.Content).NotEmpty().When(x => x.AttachmentFileIds is not { Count: > 0 }).WithMessage("Vui lòng nhập nội dung bình luận.").MaximumLength(5000).WithMessage("Bình luận tối đa 5000 ký tự.");
        RuleFor(x => x.AttachmentFileIds).Must(a => a is null || a.Count <= 10).WithMessage("Tối đa 10 tệp đính kèm mỗi bình luận.");
    }
}

public sealed class AddCommentHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IUserNotifier notify, IAuditLogger audit)
    : IRequestHandler<AddCommentCommand, CommentDto>
{
    public async Task<CommentDto> Handle(AddCommentCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, ResourceType.File, cmd.FileId, AccessLevel.View, ct);
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == cmd.FileId, ct);
        if (cmd.ParentId.HasValue && !await db.Comments.AnyAsync(c => c.Id == cmd.ParentId && c.FileId == cmd.FileId, ct))
            throw new NotFoundException("Không tìm thấy bình luận được trả lời.");
        // Attachments must be files the author can download (typically just uploaded to "Tài liệu của tôi").
        var attachments = new List<Guid>();
        foreach (var a in cmd.AttachmentFileIds?.Distinct() ?? [])
        {
            await auth.EnsureAsync(uid, ResourceType.File, a, AccessLevel.Download, ct);
            attachments.Add(a);
        }
        var mentions = (cmd.Mentions ?? []).Distinct().ToList();
        mentions = await db.Users.Where(u => mentions.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var comment = new Comment { FileId = cmd.FileId, ParentId = cmd.ParentId, Content = cmd.Content?.Trim() ?? "", Mentions = mentions, AttachmentFileIds = attachments };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);
        var dto = (await CommentMapping.MapAsync(db, [comment], uid, ct))[0];
        await notify.BroadcastToGroupAsync($"file:{cmd.FileId}", "comment", new { action = "added", comment = dto with { CanEdit = false } }, ct);

        var author = dto.AuthorName;
        var preview = comment.Content.Length > 120 ? comment.Content[..120] + "…" : comment.Content;
        var link = $"/files/{file.Id}?tab=discussion";
        foreach (var m in mentions.Where(m => m != uid))
            if (await auth.CanAsync(m, ResourceType.File, file.Id, AccessLevel.View, ct))
                await notify.NotifyUserAsync(m, "mention", $"{author} đã nhắc đến bạn trong \"{file.Name}\"", preview, link, ct);
        var notifyOwner = new HashSet<Guid>();
        if (file.CreatedBy is { } owner && owner != uid && !mentions.Contains(owner)) notifyOwner.Add(owner);
        if (cmd.ParentId is { } pid && await db.Comments.Where(c => c.Id == pid).Select(c => c.CreatedBy).FirstOrDefaultAsync(ct) is { } parentAuthor && parentAuthor != uid && !mentions.Contains(parentAuthor))
            notifyOwner.Add(parentAuthor);
        foreach (var o in notifyOwner)
            await notify.NotifyUserAsync(o, "comment", $"{author} đã bình luận về \"{file.Name}\"", preview, link, ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Comment, ResourceType.File, file.Id, file.Name, file.WorkspaceId, new { commentId = comment.Id, mentions, attachments }), ct);
        return dto;
    }
}

public sealed record EditCommentCommand(Guid Id, string Content) : IRequest<CommentDto>;

public sealed class EditCommentHandler(IAppDbContext db, ICurrentUser me, IUserNotifier notify) : IRequestHandler<EditCommentCommand, CommentDto>
{
    public async Task<CommentDto> Handle(EditCommentCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (string.IsNullOrWhiteSpace(cmd.Content)) throw new AppException("Vui lòng nhập nội dung bình luận.");
        var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == cmd.Id && !x.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy bình luận.");
        if (c.CreatedBy != uid) throw new ForbiddenException("Chỉ được sửa bình luận của mình.");
        c.Content = cmd.Content.Trim();
        c.IsEdited = true;
        await db.SaveChangesAsync(ct);
        var dto = (await CommentMapping.MapAsync(db, [c], uid, ct))[0];
        await notify.BroadcastToGroupAsync($"file:{c.FileId}", "comment", new { action = "edited", comment = dto with { CanEdit = false } }, ct);
        return dto;
    }
}

public sealed record DeleteCommentCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteCommentHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IUserNotifier notify) : IRequestHandler<DeleteCommentCommand, Unit>
{
    public async Task<Unit> Handle(DeleteCommentCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == cmd.Id && !x.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy bình luận.");
        if (c.CreatedBy != uid) await auth.EnsureAsync(uid, ResourceType.File, c.FileId, AccessLevel.Manage, ct);
        c.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await notify.BroadcastToGroupAsync($"file:{c.FileId}", "comment", new { action = "deleted", id = c.Id }, ct);
        return Unit.Value;
    }
}

/// <summary>Signed URL to an attachment: allowed for anyone who can view the commented file.</summary>
public sealed record GetAttachmentUrlQuery(Guid CommentId, Guid FileId, bool Inline) : IRequest<DownloadUrlDto>;

public sealed class GetAttachmentUrlHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<GetAttachmentUrlQuery, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> Handle(GetAttachmentUrlQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var c = await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.CommentId && !x.IsDeleted, ct) ?? throw new NotFoundException("Không tìm thấy bình luận.");
        if (!c.AttachmentFileIds.Contains(q.FileId)) throw new NotFoundException("Không tìm thấy tệp đính kèm.");
        await auth.EnsureAsync(uid, ResourceType.File, c.FileId, AccessLevel.View, ct);
        var f = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.FileId && !x.IsDeleted, ct) ?? throw new NotFoundException("Tệp đính kèm đã bị xóa.");
        await ScanGate.EnsureAsync(db, f, f.CurrentVersionId, uid, ct);
        var token = tickets.Issue(new ContentTicket(uid, "attachment", f.Id, f.CurrentVersionId, q.Inline, [new ItemRef("comment", c.Id)], null), TimeSpan.FromHours(1));
        await audit.LogAsync(new AuditEntry(q.Inline ? AuditAction.View : AuditAction.Download, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { attachmentOf = c.FileId }), ct);
        return new DownloadUrlDto($"/api/v1/content/{Uri.EscapeDataString(token)}", clock.GetUtcNow().UtcDateTime.AddHours(1), f.Name, f.MimeType, f.SizeBytes);
    }
}

/// <summary>#19 "tệp đính kèm": attachments posted in discussions of files the user can see.</summary>
public sealed record ListMyAttachmentsQuery(int Take = 100) : IRequest<IReadOnlyList<AttachmentListItemDto>>;

public sealed record AttachmentListItemDto(Guid CommentId, Guid OnFileId, string OnFileName, CommentAttachmentDto Attachment, string AuthorName, DateTime PostedAt);

public sealed class ListMyAttachmentsHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<ListMyAttachmentsQuery, IReadOnlyList<AttachmentListItemDto>>
{
    public async Task<IReadOnlyList<AttachmentListItemDto>> Handle(ListMyAttachmentsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var rows = await db.Comments.AsNoTracking().Where(c => !c.IsDeleted && c.AttachmentFileIds.Count > 0).OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync(ct);
        var result = new List<AttachmentListItemDto>();
        var mapped = await CommentMapping.MapAsync(db, rows, uid, ct);
        var fileNames = await db.Files.AsNoTracking().Where(f => rows.Select(r => r.FileId).Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        foreach (var c in mapped)
        {
            if (result.Count >= q.Take) break;
            if (!fileNames.ContainsKey(c.FileId) || !await auth.CanAsync(uid, ResourceType.File, c.FileId, AccessLevel.View, ct)) continue;
            result.AddRange(c.Attachments.Select(a => new AttachmentListItemDto(c.Id, c.FileId, fileNames[c.FileId], a, c.AuthorName, c.CreatedAt)));
        }
        return result;
    }
}

// ---------- Group workspace: summary (#29) and per-member history (#31) ----------
public sealed record MemberContributionDto(Guid UserId, string FullName, int FileCount, long TotalBytes, DateTime? LastUploadAt);

public sealed record WorkspaceSummaryDto(int TotalFiles, long TotalBytes, IReadOnlyList<MemberContributionDto> Members, PagedResult<DriveItemDto> Files);

public sealed record GetWorkspaceSummaryQuery(Guid WorkspaceId, Guid? MemberId, MediaType? MediaType) : PageQuery, IRequest<WorkspaceSummaryDto>;

public sealed class GetWorkspaceSummaryHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets) : IRequestHandler<GetWorkspaceSummaryQuery, WorkspaceSummaryDto>
{
    public async Task<WorkspaceSummaryDto> Handle(GetWorkspaceSummaryQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var access = await auth.EnsureAsync(uid, ResourceType.Workspace, q.WorkspaceId, AccessLevel.View, ct);
        var files = DriveProjection.VisibleApproval(db.Files.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && !f.IsDeleted), access, uid);
        var stats = await files.GroupBy(f => f.CreatedBy).Select(g => new { UserId = g.Key, Count = g.Count(), Bytes = g.Sum(f => f.SizeBytes), Last = g.Max(f => f.CreatedAt) }).ToListAsync(ct);
        var memberIds = await db.WorkspaceMembers.Where(m => m.WorkspaceId == q.WorkspaceId).Select(m => m.UserId).ToListAsync(ct);
        var allIds = memberIds.Concat(stats.Where(s => s.UserId.HasValue).Select(s => s.UserId!.Value)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => allIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var members = allIds.Select(id =>
        {
            var s = stats.FirstOrDefault(x => x.UserId == id);
            return new MemberContributionDto(id, names.GetValueOrDefault(id, ""), s?.Count ?? 0, s?.Bytes ?? 0, s?.Last);
        }).OrderByDescending(m => m.TotalBytes).ToList();
        var filtered = files;
        if (q.MemberId.HasValue) filtered = filtered.Where(f => f.CreatedBy == q.MemberId);
        if (q.MediaType.HasValue) filtered = filtered.Where(f => f.MediaType == q.MediaType);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            filtered = filtered.Where(f => DbFn.Unaccent(f.Name).Contains(term));
        }
        var page = await DriveProjection.Files(db, DriveProjection.SortFiles(filtered, q.Sort ?? "-createdAt"), uid).ToPagedAsync(q, ct);
        return new WorkspaceSummaryDto(stats.Sum(s => s.Count), stats.Sum(s => s.Bytes), members, page with { Items = ThumbnailUrls.Apply(page.Items, tickets, uid) });
    }
}

public sealed record ActivityDto(long Id, DateTime OccurredAt, Guid? UserId, string? Username, string? FullName, AuditAction Action, ResourceType? ResourceType, Guid? ResourceId, string? ResourceName, string? Detail);

/// <summary>#31: editing timeline of a group workspace, filterable by member.</summary>
public sealed record GetWorkspaceActivityQuery(Guid WorkspaceId, Guid? UserId, AuditAction? Action, DateTime? From, DateTime? To) : PageQuery, IRequest<PagedResult<ActivityDto>>;

public sealed class GetWorkspaceActivityHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth) : IRequestHandler<GetWorkspaceActivityQuery, PagedResult<ActivityDto>>
{
    private static readonly AuditAction[] ContentActions =
    [
        AuditAction.Upload, AuditAction.Edit, AuditAction.Create, AuditAction.Rename, AuditAction.Move, AuditAction.Copy, AuditAction.Delete,
        AuditAction.Restore, AuditAction.Purge, AuditAction.VersionRestore, AuditAction.Comment, AuditAction.Share, AuditAction.Unshare,
        AuditAction.Approve, AuditAction.Reject, AuditAction.PermissionChange,
    ];

    public async Task<PagedResult<ActivityDto>> Handle(GetWorkspaceActivityQuery q, CancellationToken ct)
    {
        await auth.EnsureAsync(me.RequireUserId(), ResourceType.Workspace, q.WorkspaceId, AccessLevel.View, ct);
        var query = db.AuditLogs.AsNoTracking().Where(a => a.WorkspaceId == q.WorkspaceId && ContentActions.Contains(a.Action));
        if (q.UserId.HasValue) query = query.Where(a => a.UserId == q.UserId);
        if (q.Action.HasValue) query = query.Where(a => a.Action == q.Action);
        if (q.From.HasValue) query = query.Where(a => a.OccurredAt >= q.From);
        if (q.To.HasValue) query = query.Where(a => a.OccurredAt < q.To.Value.AddDays(1));
        return await query.OrderByDescending(a => a.OccurredAt)
            .Select(a => new ActivityDto(a.Id, a.OccurredAt, a.UserId, a.Username, db.Users.Where(u => u.Id == a.UserId).Select(u => u.FullName).FirstOrDefault(),
                a.Action, a.ResourceType, a.ResourceId, a.ResourceName, a.Detail)).ToPagedAsync(q, ct);
    }
}
