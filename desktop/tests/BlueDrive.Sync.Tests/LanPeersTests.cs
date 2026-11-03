using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BlueDrive.Sync.Client;
using FluentAssertions;

namespace BlueDrive.Sync.Tests;

/// <summary>Sync-Phase 12 LAN transfer protocol: only a confirmed ticket, the right key and the exact content give a file.</summary>
public sealed class LanPeersTests : IAsyncLifetime
{
    private static readonly Uri Server = new("https://bluedrive.example/");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bd-lan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly byte[] _data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 7);
    private readonly Guid _fileId = Guid.NewGuid();
    private readonly string _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private string _path = "";
    private string Sha => Convert.ToHexStringLower(SHA256.HashData(_data));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "báo cáo.pdf");
        await File.WriteAllBytesAsync(_path, _data);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>A serving peer whose server confirms "good" only, with <paramref name="key"/>; it has the file at <see cref="_path"/>.</summary>
    private LanPeers Server_(string key, string? sha = null) => new(Server,
        (ticket, _) => ticket == "good" ? Task.FromResult(new LanTicketInfo(_fileId, key, sha ?? Sha, _data.Length)) : throw new InvalidOperationException("404"),
        (id, hash) => id == _fileId && hash == Sha ? _path : null);

    private static LanPeers Receiver(int port)
    {
        var r = new LanPeers(Server, (_, _) => throw new InvalidOperationException(), (_, _) => null);
        r.AddPeer(new IPEndPoint(IPAddress.Loopback, port));
        return r;
    }

    private LanTicket Ticket(string ticket = "good") => new(ticket, _key, Sha, _data.Length);

    [Fact]
    public async Task The_file_arrives_encrypted_and_checked()
    {
        await using var peer = Server_(_key);
        peer.Start(discovery: false, IPAddress.Loopback);
        await using var me = Receiver(peer.Port);
        using var got = new MemoryStream();
        (await me.TryDownloadAsync(_ => Task.FromResult(Ticket()), got, CancellationToken.None)).Should().BeTrue();
        got.ToArray().Should().Equal(_data);
        (peer.Served, me.Received).Should().Be((1, 1));
    }

    [Fact]
    public async Task Anything_wrong_means_downloading_from_the_server()
    {
        await using var peer = Server_(_key);
        peer.Start(discovery: false, IPAddress.Loopback);
        await using var me = Receiver(peer.Port);
        using var got = new MemoryStream();

        // The server did not confirm the ticket: the peer sends nothing.
        (await me.TryDownloadAsync(_ => Task.FromResult(Ticket("forged")), got, CancellationToken.None)).Should().BeFalse();
        // The receiver's key differs (someone in the middle): decryption fails.
        (await me.TryDownloadAsync(_ => Task.FromResult(Ticket() with { Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }), got,
            CancellationToken.None)).Should().BeFalse();
        // The ticket is for another version: the peer has no such copy.
        (await me.TryDownloadAsync(_ => Task.FromResult(Ticket() with { Sha256 = new string('0', 64) }), got, CancellationToken.None)).Should().BeFalse();
        // The local copy changed after it was synced: the hash does not match, nothing is written.
        await using var stale = Server_(_key, sha: Sha);
        stale.Start(discovery: false, IPAddress.Loopback);
        await using var me2 = Receiver(stale.Port);
        var original = await File.ReadAllBytesAsync(_path);
        await File.WriteAllBytesAsync(_path, [.. original[..^1], (byte)(original[^1] ^ 1)]);
        (await me2.TryDownloadAsync(_ => Task.FromResult(Ticket()), got, CancellationToken.None)).Should().BeFalse();
        got.Length.Should().Be(0, "nothing reaches the destination unless it is complete and checked");
        (peer.Served + stale.Served, me.Received + me2.Received).Should().Be((2, 0), "sent with the wrong key and the changed copy, both refused by the receiver");
    }

    [Fact]
    public async Task Computers_of_the_same_server_find_each_other_and_ignore_other_servers()
    {
        await using var a = Server_(_key);
        await using var b = new LanPeers(Server, (_, _) => throw new InvalidOperationException(), (_, _) => null);
        await using var other = new LanPeers(new Uri("https://khac.example/"), (_, _) => throw new InvalidOperationException(), (_, _) => null);
        a.Start();
        b.Start();
        other.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!(a.HasPeers && b.HasPeers) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
        if (!a.HasPeers) return; // this network does not deliver broadcasts back to the same computer (CI containers): nothing to check
        b.HasPeers.Should().BeTrue();
        other.HasPeers.Should().BeFalse("a BlueDrive of another server is not a peer");
        using var got = new MemoryStream();
        (await b.TryDownloadAsync(_ => Task.FromResult(Ticket()), got, CancellationToken.None)).Should().BeTrue();
        got.ToArray().Should().Equal(_data);
    }

    [Fact]
    public async Task No_peer_or_a_dead_peer_costs_no_ticket_or_little_time()
    {
        await using var alone = new LanPeers(Server, (_, _) => throw new InvalidOperationException(), (_, _) => null);
        var asked = false;
        (await alone.TryDownloadAsync(_ => { asked = true; return Task.FromResult(Ticket()); }, new MemoryStream(), CancellationToken.None)).Should().BeFalse();
        asked.Should().BeFalse("no ticket (and no audited download) without a peer");

        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop(); // nobody listens there any more
        await using var me = Receiver(port);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await me.TryDownloadAsync(_ => Task.FromResult(Ticket()), new MemoryStream(), CancellationToken.None)).Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
