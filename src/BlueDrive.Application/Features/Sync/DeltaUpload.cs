using System.Security.Cryptography;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using BlueDrive.Sync.Core;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Sync;

/// <summary>One chunk of a file: SHA-256 (hex) and length (FastCDC boundaries, see <see cref="FastCdc"/>).</summary>
public sealed record DeltaChunkDto(string H, int L);

public sealed record ChunkListDto(long Revision, long Size, IReadOnlyList<DeltaChunkDto> Chunks);

/// <summary>
/// Sync-Phase 12 (delta upload): the chunks of the current version, so a desktop app that changed part of a large file only sends the
/// chunks the server does not have. Needs the right to download the file (chunk hashes describe its content).
/// </summary>
public sealed record GetFileChunksQuery(Guid FileId) : IRequest<ChunkListDto>;

public sealed class GetFileChunksHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, SyncPolicyService policy, IFileContentService content)
    : IRequestHandler<GetFileChunksQuery, ChunkListDto>
{
    public async Task<ChunkListDto> Handle(GetFileChunksQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        await auth.EnsureAsync(uid, ResourceType.File, q.FileId, AccessLevel.Download, ct);
        var f = await db.Files.AsNoTracking().Where(x => x.Id == q.FileId && !x.IsDeleted).Select(x => new { x.Revision, x.CurrentVersionId }).FirstAsync(ct);
        var version = await db.FileVersions.AsNoTracking().FirstAsync(v => v.Id == f.CurrentVersionId, ct);
        await using var stream = await content.OpenReadAsync(version, ct);
        var chunks = await FastCdc.SplitAllAsync(stream, ct);
        return new ChunkListDto(f.Revision, version.SizeBytes, chunks.Select(c => new DeltaChunkDto(c.Hash, c.Length)).ToList());
    }
}

public sealed record AssembledDelta(Guid WorkspaceId, string FileName, long Length);

/// <summary>
/// Rebuilds the new content of <paramref name="FileId"/> into <paramref name="TargetPath"/>: chunks listed in <paramref name="Manifest"/> come
/// from the current version when it has them, otherwise, in order, from <paramref name="NewChunksPath"/> (each checked against its hash).
/// The caller then stores it like any upload (validation, quota, base ETag, content hash, version, audit).
/// </summary>
public sealed record AssembleDeltaCommand(Guid FileId, IReadOnlyList<DeltaChunkDto> Manifest, string NewChunksPath, string TargetPath) : IRequest<AssembledDelta>;

public sealed class AssembleDeltaHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, SyncPolicyService policy, IFileContentService content)
    : IRequestHandler<AssembleDeltaCommand, AssembledDelta>
{
    public async Task<AssembledDelta> Handle(AssembleDeltaCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await policy.EnsureAllowedAsync(uid, ct);
        await auth.EnsureAsync(uid, ResourceType.File, cmd.FileId, AccessLevel.Edit, ct);
        if (cmd.Manifest.Count == 0 || cmd.Manifest.Any(c => c.L <= 0 || c.L > FastCdc.MaxSize || c.H.Length != 64))
            throw new AppException("Danh sách khối không hợp lệ.", 422);
        var f = await db.Files.AsNoTracking().Where(x => x.Id == cmd.FileId && !x.IsDeleted).Select(x => new { x.WorkspaceId, x.Name, x.CurrentVersionId }).FirstAsync(ct);
        var version = await db.FileVersions.AsNoTracking().FirstAsync(v => v.Id == f.CurrentVersionId, ct);

        await using var baseStream = await content.OpenReadAsync(version, ct);
        var known = new Dictionary<string, (long Offset, int Length)>(StringComparer.OrdinalIgnoreCase);
        await foreach (var c in FastCdc.SplitAsync(baseStream, ct)) known.TryAdd(c.Hash, (c.Offset, c.Length));

        await using var fresh = File.OpenRead(cmd.NewChunksPath);
        await using var output = File.Create(cmd.TargetPath);
        var buffer = new byte[FastCdc.MaxSize];
        long length = 0;
        foreach (var chunk in cmd.Manifest)
        {
            var span = buffer.AsMemory(0, chunk.L);
            if (known.TryGetValue(chunk.H, out var at) && at.Length == chunk.L)
            {
                baseStream.Position = at.Offset;
                await baseStream.ReadExactlyAsync(span, ct);
            }
            else
            {
                try { await fresh.ReadExactlyAsync(span, ct); }
                catch (EndOfStreamException) { throw new AppException("Thiếu dữ liệu khối mới.", 422); }
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(span.Span)), chunk.H, StringComparison.OrdinalIgnoreCase))
                    throw new AppException("Dữ liệu khối không khớp mã băm.", 422);
            }
            await output.WriteAsync(span, ct);
            length += chunk.L;
        }
        if (fresh.Position != fresh.Length) throw new AppException("Có dữ liệu khối thừa.", 422);
        return new AssembledDelta(f.WorkspaceId, f.Name, length);
    }
}
