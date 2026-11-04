using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Search;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace BlueDrive.Infrastructure.Search;

/// <summary>
/// PostgreSQL full-text search on files.search_vector and on the text inside documents (file_texts.content_vector), unaccented,
/// 'simple' config, with prefix matching, plus an unaccented substring fallback on the name. The access scope is expressed in SQL; the handler re-checks each hit.
/// </summary>
public sealed class FileSearchService(AppDbContext db, TimeProvider clock) : IFileSearchService
{
    public async Task<(IReadOnlyList<(Guid FileId, double Rank)> Page, int Total, bool TotalCapped)> SearchAsync(SearchCriteria c, PrincipalSet p, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        IQueryable<StoredFile> q = db.Files.AsNoTracking().Where(f => !f.IsDeleted && f.ScanStatus != ScanStatus.Infected);

        if (!p.IsDocAdmin)
        {
            var uid = p.UserId;
            var principals = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(uid).ToList();
            var wsIds = db.Workspaces.Where(w => w.OwnerId == uid || w.Members.Any(m => m.UserId == uid) ||
                db.ResourcePermissions.Any(a => a.ResourceType == ResourceType.Workspace && a.ResourceId == w.Id && principals.Contains(a.PrincipalId) && (a.ExpiresAt == null || a.ExpiresAt > now)))
                .Select(w => w.Id);
            var folderGrants = db.ResourcePermissions.Where(a => a.ResourceType == ResourceType.Folder && principals.Contains(a.PrincipalId) && (a.ExpiresAt == null || a.ExpiresAt > now))
                .Select(a => a.ResourceId)
                .Concat(db.Shares.Where(s => s.ResourceType == ResourceType.Folder && principals.Contains(s.SharedWithId)).Select(s => s.ResourceId));
            var fileGrants = db.ResourcePermissions.Where(a => a.ResourceType == ResourceType.File && principals.Contains(a.PrincipalId) && (a.ExpiresAt == null || a.ExpiresAt > now))
                .Select(a => a.ResourceId)
                .Concat(db.Shares.Where(s => s.ResourceType == ResourceType.File && principals.Contains(s.SharedWithId)).Select(s => s.ResourceId));
            q = q.Where(f => wsIds.Contains(f.WorkspaceId) || fileGrants.Contains(f.Id) ||
                             (f.Folder != null && folderGrants.Any(g => f.Folder.Path.Contains("/" + g.ToString() + "/"))));
            // Unapproved items: only their uploader and workspace editors/approvers see them.
            WorkspaceMemberRole[] reviewers = [WorkspaceMemberRole.Owner, WorkspaceMemberRole.Manager, WorkspaceMemberRole.Editor, WorkspaceMemberRole.Approver];
            q = q.Where(f => f.ApprovalStatus == ApprovalStatus.Approved || f.CreatedBy == uid ||
                             db.WorkspaceMembers.Any(m => m.WorkspaceId == f.WorkspaceId && m.UserId == uid && reviewers.Contains(m.MemberRole)));
        }

        if (c.WorkspaceId is { } ws) q = q.Where(f => f.WorkspaceId == ws);
        if (c.FolderId is { } fid)
        {
            var path = await db.Folders.Where(x => x.Id == fid).Select(x => x.Path).FirstOrDefaultAsync(ct) ?? "/none/";
            q = q.Where(f => f.Folder != null && f.Folder.Path.StartsWith(path));
        }
        if (c.MediaTypes is { Count: > 0 } types) q = q.Where(f => types.Contains(f.MediaType));
        if (c.Extensions is { Count: > 0 } exts)
        {
            var lower = exts.Select(e => e.TrimStart('.').ToLowerInvariant()).ToList();
            q = q.Where(f => lower.Contains(f.Extension));
        }
        if (c.CatalogItemIds is { Count: > 0 } items)
        {
            // OR inside a catalog (e.g. two topics), AND across catalogs (topic AND campaign).
            var groups = await db.CatalogItems.Where(i => items.Contains(i.Id)).GroupBy(i => i.CatalogId).Select(g => g.Select(i => i.Id).ToList()).ToListAsync(ct);
            foreach (var g in groups) q = q.Where(f => f.CatalogItems.Any(ci => g.Contains(ci.CatalogItemId)));
        }
        if (c.OrganizationId is { } org)
        {
            if (c.IncludeSubOrganizations)
            {
                var orgPath = await db.Organizations.Where(o => o.Id == org).Select(o => o.Path).FirstOrDefaultAsync(ct) ?? "/none/";
                var orgIds = db.Organizations.Where(o => o.Path.StartsWith(orgPath)).Select(o => (Guid?)o.Id);
                q = q.Where(f => orgIds.Contains(f.UploaderOrganizationId));
            }
            else q = q.Where(f => f.UploaderOrganizationId == org);
        }
        if (c.CreatedBy is { } by) q = q.Where(f => f.CreatedBy == by);
        // Dates are calendar days in Vietnam time (UTC+7).
        if (c.From is { } from) q = q.Where(f => f.CreatedAt >= from.Date.AddHours(-7));
        if (c.To is { } to) q = q.Where(f => f.CreatedAt < to.Date.AddDays(1).AddHours(-7));
        if (c.MinSize is { } min) q = q.Where(f => f.SizeBytes >= min);
        if (c.MaxSize is { } max) q = q.Where(f => f.SizeBytes <= max);
        if (!string.IsNullOrWhiteSpace(c.Tag)) q = q.Where(f => f.Tags.Contains(c.Tag.Trim()));

        var terms = SearchText.Terms(c.Q);
        var page = Math.Max(1, c.Page);
        var size = Math.Clamp(c.PageSize, 1, 100);
        if (terms.Count > 0)
        {
            var tsQuery = string.Join(" & ", terms.Select(t => t + ":*"));
            var phrase = string.Join(' ', terms);
            // Name, title, description, tags — and the text inside the document (file_texts, weight D: ranks after the name).
            var matched = q.Where(f => EF.Property<NpgsqlTsVector>(f, "SearchVector").Matches(EF.Functions.ToTsQuery("simple", tsQuery))
                                       || DbFn.Unaccent(f.Name).Contains(phrase)
                                       || db.FileTexts.Any(t => t.FileId == f.Id && EF.Property<NpgsqlTsVector>(t, "ContentVector").Matches(EF.Functions.ToTsQuery("simple", tsQuery))));
            var ranked = matched.Select(f => new
            {
                f.Id, f.CreatedAt, f.Name, f.SizeBytes,
                Rank = EF.Property<NpgsqlTsVector>(f, "SearchVector").Rank(EF.Functions.ToTsQuery("simple", tsQuery))
                       + (db.FileTexts.Where(t => t.FileId == f.Id).Select(t => (float?)EF.Property<NpgsqlTsVector>(t, "ContentVector").Rank(EF.Functions.ToTsQuery("simple", tsQuery))).FirstOrDefault() ?? 0f),
            });
            ranked = c.Sort switch
            {
                "-createdAt" => ranked.OrderByDescending(x => x.CreatedAt),
                "createdAt" => ranked.OrderBy(x => x.CreatedAt),
                "name" => ranked.OrderBy(x => x.Name),
                "-name" => ranked.OrderByDescending(x => x.Name),
                "-size" => ranked.OrderByDescending(x => x.SizeBytes),
                "size" => ranked.OrderBy(x => x.SizeBytes),
                _ => ranked.OrderByDescending(x => x.Rank).ThenByDescending(x => x.CreatedAt),
            };
            var rows = await ranked.Skip((page - 1) * size).Take(size).ToListAsync(ct);
            var (total, capped) = await matched.CappedTotalAsync(page, size, rows.Count, ct);
            return (rows.Select(r => (r.Id, (double)r.Rank)).ToList(), total, capped);
        }
        else
        {
            q = c.Sort switch
            {
                "createdAt" => q.OrderBy(f => f.CreatedAt),
                "name" => q.OrderBy(f => f.Name),
                "-name" => q.OrderByDescending(f => f.Name),
                "-size" => q.OrderByDescending(f => f.SizeBytes),
                "size" => q.OrderBy(f => f.SizeBytes),
                _ => q.OrderByDescending(f => f.CreatedAt),
            };
            var rows = await q.Skip((page - 1) * size).Take(size).Select(f => f.Id).ToListAsync(ct);
            var (total, capped) = await q.CappedTotalAsync(page, size, rows.Count, ct);
            return (rows.Select(id => (id, 0d)).ToList(), total, capped);
        }
    }
}
