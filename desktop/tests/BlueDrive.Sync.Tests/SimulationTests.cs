using BlueDrive.Sync.Core;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;

namespace BlueDrive.Sync.Tests;

/// <summary>
/// Property-based simulation (spec 12.2): random interleavings of edits on two computers and on the web, with flaky network
/// (failures before or after the server acted) and crashes, then convergence. Invariants:
///  1. after convergence both computers and the server hold the same tree;
///  2. the engine never destroys content that the server does not hold and that has no other copy (no data loss);
///  3. a converged state stays quiet: one more pass on each computer performs no operation (no ping-pong).
/// SYNC_SIM_RUNS sets the number of sequences (CI default 300; 10,000 for the release check).
/// </summary>
public class SimulationTests
{
    private enum Kind { Write, SaveAsNew, Mkdir, Move, Delete, Sync, Flaky, Crash }

    private sealed record Op(int Actor, Kind Kind, int A, int B, int Content);

    private static readonly string[] Folders = ["F1", "F2", "F1/G"];
    private static readonly string[] Files = ["a.txt", "b.txt", "F1/c.txt", "F2/d.txt", "F1/G/e.txt", "F2/A.TXT"];
    private static readonly string[] Targets = ["a.txt", "b.txt", "F1/c.txt", "F2/d.txt", "F1/G/e.txt", "F1/G/z.txt", "F3", "F2/F1"];

    private static Gen<Op> OpGen =>
        from actor in Gen.Choose(0, 2)
        from kind in Gen.Frequency(
            (6, Gen.Constant(Kind.Write)), (1, Gen.Constant(Kind.SaveAsNew)), (2, Gen.Constant(Kind.Mkdir)), (3, Gen.Constant(Kind.Move)),
            (2, Gen.Constant(Kind.Delete)), (6, Gen.Constant(Kind.Sync)), (1, Gen.Constant(Kind.Flaky)), (1, Gen.Constant(Kind.Crash)))
        from a in Gen.Choose(0, 99)
        from b in Gen.Choose(0, 99)
        from content in Gen.Choose(0, 6)
        select new Op(actor, kind, a, b, content);

    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("SYNC_SIM_RUNS"), out var n) ? n : 300;

    [Fact]
    public void Random_interleavings_converge_without_data_loss_or_ping_pong()
    {
        var ops = Gen.Choose(5, 40).SelectMany(n => Gen.ListOf(OpGen, n)).Select(l => l.ToArray());
        Prop.ForAll(ops.ToArbitrary(), seq =>
        {
            try { Run(seq, seed: seq.Length * 7919 + seq.Sum(o => o.A)); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { throw new InvalidOperationException($"Chuỗi: {Describe(seq)} ||| {ex}", ex); }
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(Runs));
    }

    /// <summary>Replays one sequence printed by a failing run (SYNC_SIM_REPRO="B.Sync(85,98,4); …").</summary>
    [Fact]
    public void Replay_a_reported_sequence()
    {
        var text = Environment.GetEnvironmentVariable("SYNC_SIM_REPRO");
        if (string.IsNullOrWhiteSpace(text)) return;
        var seq = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^(A|B|Web)\.(\w+)\((\d+),(\d+),(\d+)\)$");
            return new Op(m.Groups[1].Value switch { "A" => 0, "B" => 1, _ => 2 }, Enum.Parse<Kind>(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value));
        }).ToArray();
        Run(seq, seq.Length * 7919 + seq.Sum(o => o.A), verbose: true);
    }

    private static void Run(Op[] seq, int seed, bool verbose = false)
    {
        var clock = new FakeClock(new DateTime(2026, 11, 3, 7, 0, 0, DateTimeKind.Utc));
        var remote = new FakeRemoteStore(clock) { Chaos = new Random(seed) };
        using var a = new Device("PC-A", remote, clock);
        using var b = new Device("PC-B", remote, clock);
        var devices = new[] { a, b };
        if (verbose) foreach (var d in devices) { d.Verbose = true; d.NewEngine(); }
        var violations = new List<string>();
        foreach (var d in devices)
        {
            var dev = d;
            dev.Fs.Destroyed = (path, hash) =>
            {
                if (!remote.EverStored.Contains(hash) && !dev.Fs.Files.Any(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase) && Content.Hash(dev.Fs.Read(f)) == hash))
                    violations.Add($"{dev.Name} đã xóa/ghi đè nội dung chưa có trên máy chủ: {path}");
            };
        }

        foreach (var op in seq)
        {
            var data = Content.Of($"nội dung {op.Content} #{op.A}");
            if (op.Kind == Kind.Flaky) { remote.FailureRate = remote.FailureRate > 0 ? 0 : 0.3; continue; }
            if (op.Actor == 2) { Web(remote, op, data); continue; }
            var dev = devices[op.Actor];
            if (verbose) dev.Db.Log("NGƯỜI DÙNG", $"{op.Kind}({op.A},{op.B},{op.Content})", string.Join(" ", dev.Fs.Files.Select(f => $"{f}={Content.Hash(dev.Fs.Read(f))[..6]}")));
            switch (op.Kind)
            {
                case Kind.Sync:
                    try { dev.SyncAsync().GetAwaiter().GetResult(); }
                    catch (RemoteTransientException) { dev.NewEngine(); }
                    break;
                case Kind.Crash:
                    dev.NewEngine();
                    break;
                default:
                    User(dev.Fs, op, data);
                    break;
            }
        }

        // Every content on a computer before convergence must survive somewhere.
        var before = devices.SelectMany(d => d.Fs.Files.Where(f => !f.StartsWith(IgnoreRules.NotSyncedFolder)).Select(f => Content.Hash(d.Fs.Read(f)))).ToHashSet();

        remote.FailureRate = 0;
        for (var i = 0; i < 8; i++)
        {
            var ops = a.SyncAsync().GetAwaiter().GetResult().Operations + b.SyncAsync().GetAwaiter().GetResult().Operations;
            if (ops == 0) break;
        }

        violations.Should().BeEmpty();
        var server = remote.Tree();
        if (verbose)
        {
            const string nl = "\n  ";
            string Dump(IReadOnlyDictionary<string, string> t) =>
                string.Join(nl, t.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} = {k.Value[..Math.Min(6, k.Value.Length)]}"));
            string Log(Device d) => string.Join(nl, d.Db.Activity(80).Reverse().Select(x => $"{x.Action} {x.Path} {x.Detail}"));
            string Plan(Device d)
            {
                d.SyncAsync().GetAwaiter().GetResult();
                return string.Join(nl, d.Engine.LastPlan);
            }
            string Bases(Device d) => string.Join(nl, d.Db.Items(FakeRemoteStore.Root).Select(x => $"{x.RemoteId} parent={x.ParentRemoteId} name={x.Name} rn={x.RemoteName} fid={x.LocalFileId}"));
            string Remote(Device d) => string.Join(nl, d.Db.RemoteTree(FakeRemoteStore.Root).Values.Select(x => $"{x.Id} parent={x.ParentId} name={x.Name} rev={x.Revision}"));
            var report = $"PLAN A:{nl}{Plan(a)}\nPLAN B:{nl}{Plan(b)}\nBASE A:{nl}{Bases(a)}\nBASE B:{nl}{Bases(b)}\nREMOTE VIEW B ({b.Db.Diagnostics()}):{nl}{Remote(b)}\nREMOTE VIEW A ({a.Db.Diagnostics()}):{nl}{Remote(a)}\nIDS: {remote.Ids()}\nJOURNAL: {remote.JournalText()}\nSERVER:{nl}{Dump(server)}\nA:{nl}{Dump(Tree(a.Fs))}\nB:{nl}{Dump(Tree(b.Fs))}\nLOG A:{nl}{Log(a)}\nLOG B:{nl}{Log(b)}";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "bd-sim-report.txt"), report);
        }
        Tree(a.Fs).Should().BeEquivalentTo(server, $"PC-A khớp máy chủ. Chuỗi: {Describe(seq)}");
        Tree(b.Fs).Should().BeEquivalentTo(server, $"PC-B khớp máy chủ. Chuỗi: {Describe(seq)}");

        var seqBefore = remote.Seq;
        a.SyncAsync().GetAwaiter().GetResult().Operations.Should().Be(0, $"không ping-pong. Chuỗi: {Describe(seq)}");
        b.SyncAsync().GetAwaiter().GetResult().Operations.Should().Be(0, $"không ping-pong. Chuỗi: {Describe(seq)}");
        remote.Seq.Should().Be(seqBefore);

        var everywhere = remote.EverStored.Concat(server.Values).ToHashSet();
        before.Where(h => !everywhere.Contains(h)).Should().BeEmpty($"không mất nội dung. Chuỗi: {Describe(seq)}");
    }

    private static void User(FakeLocalFileSystem fs, Op op, byte[] data)
    {
        var file = Files[op.A % Files.Length];
        switch (op.Kind)
        {
            case Kind.Write:
                if (!AnyAncestorIsFile(fs, file)) fs.UserWrite(file, data);
                break;
            case Kind.SaveAsNew:
                fs.UserSaveAsNewFile(file, data);
                break;
            case Kind.Mkdir:
                var folder = Folders[op.A % Folders.Length];
                if (!AnyAncestorIsFile(fs, folder + "/x") && !fs.Exists(folder)) fs.UserMkdir(folder);
                break;
            case Kind.Move:
            {
                var all = fs.Files.Concat(fs.Folders).OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (all.Count == 0) break;
                var src = all[op.A % all.Count];
                var dst = Targets[op.B % Targets.Length];
                if (NameMapper.ParentOf(dst) is { } p && !fs.IsFolder(p)) break;
                fs.UserMove(src, dst);
                break;
            }
            case Kind.Delete:
            {
                var all = fs.Files.Concat(fs.Folders).OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (all.Count > 0) fs.UserDelete(all[op.A % all.Count]);
                break;
            }
        }
    }

    private static bool AnyAncestorIsFile(FakeLocalFileSystem fs, string path)
    {
        for (var p = NameMapper.ParentOf(path); p is not null; p = NameMapper.ParentOf(p))
            if (fs.Exists(p) && !fs.IsFolder(p)) return true;
        return fs.IsFolder(path);
    }

    private static void Web(FakeRemoteStore remote, Op op, byte[] data)
    {
        var tree = remote.Tree().Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
        switch (op.Kind)
        {
            case Kind.Write or Kind.SaveAsNew:
            {
                var file = Files[op.A % Files.Length];
                remote.WebUpload(NameMapper.ParentOf(file), NameMapper.NameOf(file), data);
                break;
            }
            case Kind.Mkdir:
            {
                var folder = Folders[op.A % Folders.Length];
                remote.WebCreateFolder(NameMapper.ParentOf(folder), NameMapper.NameOf(folder));
                break;
            }
            case Kind.Move when tree.Count > 0:
            {
                var src = tree[op.A % tree.Count];
                var dst = Targets[op.B % Targets.Length];
                if (op.B % 2 == 0) remote.WebRename(src, NameMapper.NameOf(dst));
                else remote.WebMove(src, NameMapper.ParentOf(dst));
                break;
            }
            case Kind.Delete when tree.Count > 0:
                remote.WebDelete(tree[op.A % tree.Count]);
                break;
        }
    }

    private static Dictionary<string, string> Tree(FakeLocalFileSystem fs) =>
        fs.Folders.Where(p => !p.StartsWith(IgnoreRules.NotSyncedFolder)).Select(p => (p, "<dir>"))
            .Concat(fs.Files.Where(p => !p.StartsWith(IgnoreRules.NotSyncedFolder)).Select(p => (p, Content.Hash(fs.Read(p)))))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    private static string Describe(Op[] seq) => string.Join("; ", seq.Select(o => $"{(o.Actor == 2 ? "Web" : o.Actor == 0 ? "A" : "B")}.{o.Kind}({o.A},{o.B},{o.Content})"));
}
