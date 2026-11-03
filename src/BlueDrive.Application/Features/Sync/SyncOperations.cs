using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Sync;

/// <summary>If-Match checks shared by the existing drive endpoints (action filter) and /sync/batch.</summary>
public static class SyncPreconditions
{
    /// <summary>Throws 412 with the item's current sync metadata when <paramref name="ifMatch"/> is stale. No header = no check.</summary>
    public static async Task CheckAsync(IAppDbContext db, SyncAccessContext access, Guid userId, Guid id, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) return;
        var revision = await db.Files.Where(f => f.Id == id).Select(f => (long?)f.Revision).FirstOrDefaultAsync(ct)
                       ?? await db.Folders.Where(f => f.Id == id).Select(f => (long?)f.Revision).FirstOrDefaultAsync(ct);
        if (revision is null) throw new NotFoundException("Không tìm thấy tài liệu hoặc thư mục.");
        if (SyncIds.Matches(ifMatch, revision.Value)) return;
        await access.InitAsync(userId, ct);
        throw new PreconditionFailedException(await SyncItems.LoadAsync(db, access, id, ct));
    }

    public static async Task<string?> CurrentETagAsync(IAppDbContext db, Guid id, CancellationToken ct)
    {
        var revision = await db.Files.Where(f => f.Id == id).Select(f => (long?)f.Revision).FirstOrDefaultAsync(ct)
                       ?? await db.Folders.Where(f => f.Id == id).Select(f => (long?)f.Revision).FirstOrDefaultAsync(ct);
        return revision is { } r ? SyncIds.ETag(r) : null;
    }
}

// ---------------------------------------------------------------- content / links

/// <summary>Signed content URL for a file version (the API answers with a redirect; Range requests are supported there).</summary>
public sealed record GetSyncContentUrlQuery(Guid Id, Guid? VersionId) : IRequest<DownloadUrlDto>;

public sealed class GetSyncContentUrlHandler(IAppDbContext db, ICurrentUser me, SyncPolicyService policy, ISender mediator) : IRequestHandler<GetSyncContentUrlQuery, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> Handle(GetSyncContentUrlQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        var p = await policy.GetAsync(ct);
        var f = await db.Files.AsNoTracking().Where(x => x.Id == q.Id && !x.IsDeleted).Select(x => new { x.Extension, x.SizeBytes }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Không tìm thấy tài liệu.");
        if (p.BlockedExtensions.Contains(f.Extension.ToLowerInvariant())) throw new ForbiddenException($"Tệp .{f.Extension} không được đồng bộ xuống máy theo chính sách.");
        if (p.MaxFileSizeBytes > 0 && f.SizeBytes > p.MaxFileSizeBytes) throw new AppException("Tệp vượt quá kích thước tối đa được đồng bộ theo chính sách.", 413);
        // Download right + audit (with device id) are enforced by the regular download query.
        return await mediator.Send(new GetDownloadUrlQuery(q.Id, q.VersionId, false), ct);
    }
}

public sealed record SyncWebUrlDto(string Url);

public sealed record GetSyncWebUrlQuery(Guid Id) : IRequest<SyncWebUrlDto>;

public sealed class GetSyncWebUrlHandler(IAppDbContext db, ICurrentUser me, SyncAccessContext access, IConfiguration config) : IRequestHandler<GetSyncWebUrlQuery, SyncWebUrlDto>
{
    public async Task<SyncWebUrlDto> Handle(GetSyncWebUrlQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await access.InitAsync(uid, ct);
        var item = await SyncItems.LoadAsync(db, access, q.Id, ct) ?? throw new NotFoundException("Không tìm thấy tài liệu hoặc bạn không có quyền truy cập.");
        var baseUrl = (config["App:PublicBaseUrl"] ?? "").TrimEnd('/');
        var personal = await db.Workspaces.AnyAsync(w => w.Id == item.WorkspaceId && w.OwnerId == uid && w.Type == WorkspaceType.Personal, ct);
        var path = item.Type == "file" ? $"/files/{item.Id}"
            : personal ? $"/my-drive?folder={item.Id}" : $"/workspaces/{item.WorkspaceId}?folder={item.Id}";
        return new SyncWebUrlDto(baseUrl + path);
    }
}

/// <summary>Context menu "Chia sẻ… / Sao chép liên kết": creates a share link (optional password / expiry) for the item.</summary>
public sealed record CreateSyncShareLinkCommand(Guid Id, bool AllowDownload, string? Password, DateTime? ExpiresAt, bool RequireLogin) : IRequest<ShareLinkDto>;

public sealed class CreateSyncShareLinkHandler(IAppDbContext db, ISender mediator) : IRequestHandler<CreateSyncShareLinkCommand, ShareLinkDto>
{
    public async Task<ShareLinkDto> Handle(CreateSyncShareLinkCommand cmd, CancellationToken ct)
    {
        var type = await db.Files.AnyAsync(f => f.Id == cmd.Id, ct) ? ResourceType.File
            : await db.Folders.AnyAsync(f => f.Id == cmd.Id, ct) ? ResourceType.Folder : throw new NotFoundException("Không tìm thấy tài liệu hoặc thư mục.");
        return await mediator.Send(new SaveShareLinkCommand(null, type, cmd.Id, cmd.AllowDownload, cmd.Password, false, cmd.ExpiresAt, null, cmd.RequireLogin), ct);
    }
}

// ---------------------------------------------------------------- batch

/// <summary>
/// One operation of /sync/batch. Ops: create_folder (workspace_id, parent_id | parent_ref, name), move (item_id, target_workspace_id,
/// target_folder_id | target_ref), rename (item_id, name), delete (item_id). if_match guards move / rename / delete.
/// </summary>
public sealed record SyncBatchOperation(string Op, string? ClientRef, Guid? ItemId, Guid? WorkspaceId, Guid? ParentId, string? ParentRef, string? Name,
    Guid? TargetWorkspaceId, Guid? TargetFolderId, string? TargetRef, string? IfMatch);

public sealed record SyncBatchResult(string? ClientRef, int Status, SyncItemDto? Item, string? Error);

public sealed record SyncBatchCommand(IReadOnlyList<SyncBatchOperation> Operations) : IRequest<IReadOnlyList<SyncBatchResult>>;

public sealed class SyncBatchHandler(IAppDbContext db, ICurrentUser me, ISender mediator, SyncAccessContext access, SyncPolicyService policy)
    : IRequestHandler<SyncBatchCommand, IReadOnlyList<SyncBatchResult>>
{
    public async Task<IReadOnlyList<SyncBatchResult>> Handle(SyncBatchCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        if (cmd.Operations.Count > 500) throw new AppException("Mỗi lô tối đa 500 thao tác.");
        var refs = new Dictionary<string, Guid>();
        var results = new List<SyncBatchResult>();
        foreach (var op in cmd.Operations)
        {
            try
            {
                Guid id;
                switch (op.Op)
                {
                    case "create_folder":
                        var parent = op.ParentRef is { } pr ? refs.GetValueOrDefault(pr) : op.ParentId;
                        if (op.ParentRef is not null && parent == Guid.Empty) throw new AppException($"Không tìm thấy thư mục cha \"{op.ParentRef}\" trong lô.");
                        var created = await mediator.Send(new CreateFolderCommand(op.WorkspaceId ?? throw new AppException("Thiếu workspace_id."), parent, op.Name ?? ""), ct);
                        id = created.Id;
                        if (op.ClientRef is not null) refs[op.ClientRef] = id;
                        break;
                    case "move":
                        id = op.ItemId ?? throw new AppException("Thiếu item_id.");
                        await SyncPreconditions.CheckAsync(db, access, uid, id, op.IfMatch, ct);
                        var target = op.TargetRef is { } tr ? refs.GetValueOrDefault(tr) : op.TargetFolderId;
                        await mediator.Send(new MoveItemsCommand([new ItemRef(await TypeOfAsync(id, ct), id)], op.TargetWorkspaceId ?? throw new AppException("Thiếu target_workspace_id."),
                            target == Guid.Empty ? null : target), ct);
                        break;
                    case "rename":
                        id = op.ItemId ?? throw new AppException("Thiếu item_id.");
                        await SyncPreconditions.CheckAsync(db, access, uid, id, op.IfMatch, ct);
                        if (await TypeOfAsync(id, ct) == "file") await mediator.Send(new RenameFileCommand(id, op.Name ?? ""), ct);
                        else await mediator.Send(new UpdateFolderCommand(id, op.Name, null), ct);
                        break;
                    case "delete":
                        id = op.ItemId ?? throw new AppException("Thiếu item_id.");
                        await SyncPreconditions.CheckAsync(db, access, uid, id, op.IfMatch, ct);
                        await mediator.Send(new DeleteItemsCommand([new ItemRef(await TypeOfAsync(id, ct), id)]), ct);
                        results.Add(new SyncBatchResult(op.ClientRef, 200, null, null));
                        continue;
                    default:
                        throw new AppException($"Thao tác \"{op.Op}\" không được hỗ trợ.");
                }
                await access.InitAsync(uid, ct);
                results.Add(new SyncBatchResult(op.ClientRef, 200, await SyncItems.LoadAsync(db, access, id, ct), null));
            }
            catch (PreconditionFailedException pf)
            {
                results.Add(new SyncBatchResult(op.ClientRef, 412, pf.Current as SyncItemDto, pf.Message));
            }
            catch (AppException ex)
            {
                results.Add(new SyncBatchResult(op.ClientRef, ex.StatusCode, null, ex.Message));
            }
        }
        return results;
    }

    private async Task<string> TypeOfAsync(Guid id, CancellationToken ct) =>
        await db.Files.AnyAsync(f => f.Id == id, ct) ? "file"
        : await db.Folders.AnyAsync(f => f.Id == id, ct) ? "folder" : throw new NotFoundException("Không tìm thấy tài liệu hoặc thư mục.");
}
