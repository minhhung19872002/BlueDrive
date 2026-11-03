using System.Security.Cryptography;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BlueDrive.Application.Features.Sync;

/// <summary>
/// LAN sync (spec Sync-Phase 12): a BlueDrive Desktop that needs a file may get it from another BlueDrive Desktop on the same network
/// instead of downloading it from the server. The server stays in charge of who gets what:
/// <list type="number">
/// <item>the device that needs the file asks for a ticket — the same checks as a download (sync policy, download right) and the download is
/// audited; the ticket carries the content hash, the size and a fresh AES-256 key;</item>
/// <item>it hands the ticket to a peer, which asks the server to confirm it (the peer must be a signed-in device that can see the file itself)
/// and gets the same hash and key back;</item>
/// <item>the peer sends the bytes it already has, encrypted with that key (AES-GCM); the receiver checks the hash and falls back to the server
/// on any failure.</item>
/// </list>
/// Tickets live 2 minutes in memory and can be confirmed by at most 3 peers.
/// </summary>
public static class LanSync
{
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    public const int MaxConfirmations = 3;

    internal sealed class Ticket
    {
        public required Guid FileId { get; init; }
        public required Guid UserId { get; init; }
        public Guid? DeviceId { get; init; }
        public required string Sha256 { get; init; }
        public required long Size { get; init; }
        public required string Key { get; init; }
        public int Confirmations;
    }

    internal static string CacheKey(string hash) => "lan-ticket:" + hash;
}

public sealed record LanTicketDto(string Ticket, string Key, string Sha256, long Size);

public sealed record LanTicketInfoDto(Guid FileId, string Key, string Sha256, long Size);

public sealed record CreateLanTicketCommand(Guid FileId) : IRequest<LanTicketDto>;

public sealed class CreateLanTicketHandler(IAppDbContext db, ICurrentUser me, ISender mediator, ITokenService tokens, IMemoryCache cache)
    : IRequestHandler<CreateLanTicketCommand, LanTicketDto>
{
    public async Task<LanTicketDto> Handle(CreateLanTicketCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (me.DeviceId is null) throw new ForbiddenException("Chỉ ứng dụng BlueDrive Desktop dùng được đồng bộ trong mạng LAN.");
        // Policy (blocked types, size), download right and the audited download: exactly as a download from the server.
        await mediator.Send(new GetSyncContentUrlQuery(cmd.FileId, null), ct);
        var f = await db.Files.AsNoTracking().Where(x => x.Id == cmd.FileId && !x.IsDeleted).Select(x => new { x.ChecksumSha256, x.SizeBytes })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy tài liệu.");
        if (string.IsNullOrEmpty(f.ChecksumSha256)) throw new ConflictException("Tệp chưa có mã băm, hãy tải từ máy chủ.");
        var ticket = tokens.GenerateOpaqueToken(32);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        cache.Set(LanSync.CacheKey(tokens.HashToken(ticket)), new LanSync.Ticket
        {
            FileId = cmd.FileId, UserId = uid, DeviceId = me.DeviceId, Sha256 = f.ChecksumSha256.ToLowerInvariant(), Size = f.SizeBytes, Key = key,
        }, LanSync.TicketLifetime);
        return new LanTicketDto(ticket, key, f.ChecksumSha256.ToLowerInvariant(), f.SizeBytes);
    }
}

public sealed record ConfirmLanTicketCommand(string Ticket) : IRequest<LanTicketInfoDto>;

public sealed class ConfirmLanTicketHandler(ICurrentUser me, IResourceAuthorizationService auth, ITokenService tokens, IMemoryCache cache)
    : IRequestHandler<ConfirmLanTicketCommand, LanTicketInfoDto>
{
    public async Task<LanTicketInfoDto> Handle(ConfirmLanTicketCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (me.DeviceId is null) throw new ForbiddenException("Chỉ ứng dụng BlueDrive Desktop dùng được đồng bộ trong mạng LAN.");
        const string invalid = "Vé đồng bộ LAN không hợp lệ hoặc đã hết hạn.";
        if (string.IsNullOrEmpty(cmd.Ticket) || !cache.TryGetValue(LanSync.CacheKey(tokens.HashToken(cmd.Ticket)), out LanSync.Ticket? t) || t is null)
            throw new NotFoundException(invalid);
        if (me.DeviceId == t.DeviceId) throw new NotFoundException(invalid); // a device does not serve itself
        if (Interlocked.Increment(ref t.Confirmations) > LanSync.MaxConfirmations) throw new NotFoundException(invalid);
        // The peer must be allowed to see the file too: it only learns which file the ticket is for when it already has it.
        var access = await auth.GetAccessAsync(uid, ResourceType.File, t.FileId, ct);
        if (!access.CanView) throw new NotFoundException(invalid);
        return new LanTicketInfoDto(t.FileId, t.Key, t.Sha256, t.Size);
    }
}
