using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueDrive.Sync.Client;

/// <summary>Ticket from the server for one LAN transfer (see the server's LanSync): handed to a peer, never to anybody else.</summary>
public sealed record LanTicket(string Ticket, string Key, string Sha256, long Size);

/// <summary>What the server confirmed to the peer that serves the file.</summary>
public sealed record LanTicketInfo(Guid FileId, string Key, string Sha256, long Size);

/// <summary>
/// LAN sync between BlueDrive Desktops of the same server (Sync-Phase 12). Peers announce themselves on the local network (UDP broadcast,
/// port <see cref="DiscoveryPort"/>) and serve files over TCP; every transfer needs a server ticket: the receiver gets it after the server
/// checked its download right (and audited the download), the serving peer has the server confirm it before sending anything. The content is
/// encrypted with the ticket's key (AES-256-GCM, 1 MiB frames, frame number authenticated, explicit end frame) and the receiver checks the
/// SHA-256 of the whole file; any failure means "download from the server instead". Only an unchanged, fully downloaded local copy is served.
/// </summary>
public sealed class LanPeers : IAsyncDisposable
{
    public const int DiscoveryPort = 47391;
    private const int FrameBytes = 1024 * 1024;
    private const int MaxServing = 4;
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromMinutes(2);

    private readonly string _network;
    private readonly Func<string, CancellationToken, Task<LanTicketInfo>> _confirm;
    private readonly Func<Guid, string, string?> _findLocal;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, (IPEndPoint EndPoint, DateTime Seen)> _peers = new();
    private readonly SemaphoreSlim _serving = new(MaxServing);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private Task? _accept, _listen, _announce;

    /// <param name="server">The BlueDrive server: peers of another server are ignored.</param>
    /// <param name="confirm">Asks the server to confirm a ticket received from a peer (throws when it is not valid).</param>
    /// <param name="findLocal">(file id, sha256) → path of an unchanged, fully downloaded local copy, or null.</param>
    public LanPeers(Uri server, Func<string, CancellationToken, Task<LanTicketInfo>> confirm, Func<Guid, string, string?> findLocal)
    {
        _network = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server.GetLeftPart(UriPartial.Authority).ToLowerInvariant())))[..16];
        _confirm = confirm;
        _findLocal = findLocal;
    }

    /// <summary>TCP port this computer serves on (0 before <see cref="Start"/>).</summary>
    public int Port { get; private set; }
    public bool HasPeers => _peers.Values.Any(p => DateTime.UtcNow - p.Seen < PeerExpiry);
    /// <summary>Files sent to / received from peers (diagnostics, tests).</summary>
    public int Served, Received;
    /// <summary>Connection timeout to a peer.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Starts serving; <paramref name="discovery"/> false: only peers added with <see cref="AddPeer"/> (tests).</summary>
    public void Start(bool discovery = true, IPAddress? bind = null)
    {
        _tcp = new TcpListener(bind ?? IPAddress.Any, 0);
        _tcp.Start();
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _accept = Task.Run(() => AcceptAsync(_stop.Token));
        if (!discovery) return;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _listen = Task.Run(() => ListenAsync(_stop.Token));
            _announce = Task.Run(() => AnnounceAsync(_stop.Token));
        }
        catch (SocketException)
        {
            // Port taken (another account of this computer listens already) or blocked: this computer still serves peers that find it.
            _udp?.Dispose();
            _udp = null;
        }
    }

    public void AddPeer(IPEndPoint endPoint) => _peers[endPoint.ToString()] = (endPoint, DateTime.MaxValue);

    // ---------------------------------------------------------------- discovery

    private sealed record Announce(int V, string Net, string Id, int Port);

    private async Task AnnounceAsync(CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new Announce(1, _network, _instance, Port));
        while (!ct.IsCancellationRequested)
        {
            try { await _udp!.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), ct); }
            catch (SocketException) { } // no network right now
            catch (ObjectDisposedException) { return; }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            Announce? a;
            try { a = JsonSerializer.Deserialize<Announce>(r.Buffer, JsonSerializerOptions.Web); }
            catch (JsonException) { continue; }
            if (a is not { V: 1 } || a.Net != _network || a.Id == _instance || a.Port is <= 0 or > 65535) continue;
            var ep = new IPEndPoint(r.RemoteEndPoint.Address, a.Port);
            _peers[a.Id] = (ep, DateTime.UtcNow);
        }
    }

    // ---------------------------------------------------------------- receiving

    /// <summary>
    /// Gets the file from a peer and writes it to <paramref name="destination"/> once complete and checked (received into a temporary file
    /// first, so a failed peer leaves nothing behind). False when no peer had it — the caller downloads from the server then.
    /// <paramref name="ticket"/> is only asked for when a peer is known.
    /// </summary>
    public async Task<bool> TryDownloadAsync(Func<CancellationToken, Task<LanTicket>> ticket, Stream destination, CancellationToken ct)
    {
        var peers = _peers.Values.Where(p => DateTime.UtcNow - p.Seen < PeerExpiry).OrderByDescending(p => p.Seen).Take(3).Select(p => p.EndPoint).ToList();
        if (peers.Count == 0) return false;
        LanTicket t;
        try { t = await ticket(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return false; }
        var key = Convert.FromBase64String(t.Key);
        var tmp = Path.Combine(Path.GetTempPath(), $"bluedrive-lan-{Guid.NewGuid():N}.part");
        try
        {
            await using var received = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            foreach (var peer in peers)
            {
                received.Position = 0;
                received.SetLength(0);
                try
                {
                    if (!await ReceiveFromAsync(peer, t, key, received, ct)) continue;
                    received.Position = 0;
                    await received.CopyToAsync(destination, ct);
                    Interlocked.Increment(ref Received);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or TimeoutException or InvalidDataException
                                               || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // no temporary space: the server then
    }

    private async Task<bool> ReceiveFromAsync(IPEndPoint peer, LanTicket t, byte[] key, Stream destination, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(peer, connect.Token);
        }
        await using var net = tcp.GetStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        await net.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { v = 1, ticket = t.Ticket }), idle.Token);
        await net.WriteAsync("\n"u8.ToArray(), idle.Token);
        var status = new byte[1];
        await net.ReadExactlyAsync(status, idle.Token);
        if (status[0] != 1) return false;

        using var aes = new AesGcm(key, 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[16];
        var tag = new byte[16];
        var aad = new byte[8];
        long index = 0, total = 0;
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            await net.ReadExactlyAsync(header, idle.Token);
            var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12));
            if (len < 0 || len > FrameBytes) throw new InvalidDataException("Khung dữ liệu LAN không hợp lệ.");
            var cipher = new byte[len];
            await net.ReadExactlyAsync(cipher, idle.Token);
            await net.ReadExactlyAsync(tag, idle.Token);
            BinaryPrimitives.WriteInt64BigEndian(aad, index++);
            var plain = new byte[len];
            aes.Decrypt(header.AsSpan(0, 12), cipher, tag, plain, aad); // throws when tampered with
            if (len == 0) break; // authenticated end: a cut connection is not mistaken for a complete file
            total += len;
            if (total > t.Size) return false;
            sha.AppendData(plain);
            await destination.WriteAsync(plain, ct);
        }
        await destination.FlushAsync(ct);
        return total == t.Size && Convert.ToHexStringLower(sha.GetHashAndReset()) == t.Sha256.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- serving

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp!.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    if (!await _serving.WaitAsync(0, ct)) return; // busy: the receiver tries another peer or the server
                    try { await ServeAsync(client, ct); }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or InvalidDataException) { }
                    finally { _serving.Release(); }
                }
            }, ct);
        }
    }

    private sealed record Request(int V, string Ticket);

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        await using var net = client.GetStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(10));
        var line = await ReadLineAsync(net, 4096, idle.Token);
        var req = JsonSerializer.Deserialize<Request>(line, JsonSerializerOptions.Web);
        if (req is not { V: 1 } || string.IsNullOrEmpty(req.Ticket))
        {
            await net.WriteAsync(new byte[] { 0 }, ct);
            return;
        }
        LanTicketInfo info;
        string? path;
        try
        {
            info = await _confirm(req.Ticket, idle.Token);
            path = _findLocal(info.FileId, info.Sha256.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            path = null;
            info = null!;
        }
        FileStream? file = null;
        try
        {
            if (path is not null) file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FrameBytes, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { file = null; }
        if (file is null || file.Length != info.Size)
        {
            if (file is not null) await file.DisposeAsync();
            await net.WriteAsync(new byte[] { 0 }, ct);
            return;
        }
        await using (file)
        {
            await net.WriteAsync(new byte[] { 1 }, ct);
            using var aes = new AesGcm(Convert.FromBase64String(info.Key), 16);
            var buffer = new byte[FrameBytes];
            var header = new byte[16];
            var tag = new byte[16];
            var aad = new byte[8];
            long index = 0;
            while (true)
            {
                var n = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                RandomNumberGenerator.Fill(header.AsSpan(0, 12));
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), n);
                BinaryPrimitives.WriteInt64BigEndian(aad, index++);
                var cipher = new byte[n];
                aes.Encrypt(header.AsSpan(0, 12), buffer.AsSpan(0, n), cipher, tag, aad);
                await net.WriteAsync(header, ct);
                await net.WriteAsync(cipher, ct);
                await net.WriteAsync(tag, ct);
                if (n == 0) break;
            }
            await net.FlushAsync(ct);
            Interlocked.Increment(ref Served);
        }
    }

    private static async Task<string> ReadLineAsync(Stream s, int max, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < max)
        {
            if (await s.ReadAsync(one, ct) == 0) break;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("Yêu cầu LAN không hợp lệ.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _tcp?.Stop();
        _udp?.Dispose();
        foreach (var t in new[] { _accept, _listen, _announce })
        {
            if (t is null) continue;
            try { await t; }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        _stop.Dispose();
        _serving.Dispose();
    }
}
