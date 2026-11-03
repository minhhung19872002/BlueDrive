using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Core;
using BlueDrive.Sync.Windows;

// BlueDrive Desktop performance measurements (spec section 10). Results: Markdown on stdout, JSON with --out.
//   bluedrive-perf scale   [--files 100000] [--folders 5000]          Files On-Demand on this computer, in-memory server
//   bluedrive-perf latency --server URL --user U --password P [--rounds 10]   real server (SignalR), full sync agent
Console.OutputEncoding = System.Text.Encoding.UTF8;
var cmd = args.FirstOrDefault() ?? "";
string Opt(string name, string def) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault() ?? def;
var results = new Dictionary<string, object>();
switch (cmd)
{
    case "scale":
        await Scale.RunAsync(int.Parse(Opt("files", "100000")), int.Parse(Opt("folders", "5000")), results);
        break;
    case "acceptance":
        await Acceptance.RunAsync(new Uri(Opt("server", "http://localhost:8088")), Opt("user", "admin"), Opt("password", ""), results);
        break;
    case "latency":
        await Latency.RunAsync(new Uri(Opt("server", "http://localhost:8088")), Opt("user", "admin"), Opt("password", ""), int.Parse(Opt("rounds", "10")), results);
        break;
    default:
        Console.Error.WriteLine("bluedrive-perf scale|latency (xem desktop/docs/bao-cao-hieu-nang.md)");
        return 2;
}
if (Opt("out", "") is { Length: > 0 } outFile)
{
    var all = File.Exists(outFile) ? JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(outFile))! : [];
    foreach (var (k, v) in results) all[k] = v;
    all["machine"] = $"{Environment.MachineName}, {Environment.ProcessorCount} CPU, {Environment.OSVersion}";
    File.WriteAllText(outFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
}
return 0;

internal static class Measure
{
    public static long ManagedMb() => GC.GetTotalMemory(forceFullCollection: true) / 1024 / 1024;

    public static long WorkingSetMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1024 / 1024;
    }

    /// <summary>Average CPU of this process (% of one core) over <paramref name="span"/> while nothing changes.</summary>
    public static async Task<double> IdleCpuAsync(TimeSpan span)
    {
        using var p = Process.GetCurrentProcess();
        var before = p.TotalProcessorTime;
        await Task.Delay(span);
        p.Refresh();
        return Math.Round((p.TotalProcessorTime - before).TotalMilliseconds / span.TotalMilliseconds * 100, 2);
    }

    public static string P(IReadOnlyList<double> values, double q) =>
        values.Count == 0 ? "-" : $"{values.OrderBy(v => v).ElementAt((int)Math.Min(values.Count - 1, Math.Ceiling(q * values.Count) - 1)):0.00}";
}

/// <summary>In-memory server with the given tree (no network: measures the client — engine, SQLite state, Cloud Files placeholders).</summary>
internal sealed class BigRemote : IRemoteStore, IRemoteRangeReader
{
    public const string Root = "perf";
    private readonly List<RemoteItem> _items = [];
    private static readonly byte[] Data = "BlueDrive perf\n"u8.ToArray();
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Data));

    public BigRemote(int files, int folders)
    {
        var now = DateTime.UtcNow;
        var top = Math.Max(1, folders / 100);
        var ids = new List<string>(folders);
        for (var i = 0; i < folders; i++)
        {
            var parent = i < top ? null : ids[(i - top) % top];
            var id = "D" + i;
            ids.Add(id);
            _items.Add(new RemoteItem(id, parent, i < top ? $"Chương trình {i:000}" : $"Ấn phẩm {i:0000}", true, 1, null, 0, now));
        }
        for (var i = 0; i < files; i++)
            _items.Add(new RemoteItem("F" + i, ids[i % folders], $"Tài liệu truyền thông {i:000000}.docx", false, 1, Hash, Data.Length, now));
    }

    public int Count => _items.Count;

    public Task<RemoteSnapshotPage> SnapshotAsync(string rootId, string? pageToken, string? folderId, CancellationToken ct)
    {
        const int page = 1000; // like the server
        var skip = pageToken is null ? 0 : int.Parse(pageToken);
        var slice = _items.Skip(skip).Take(page).ToList();
        return Task.FromResult(new RemoteSnapshotPage(slice, skip + page < _items.Count ? (skip + page).ToString() : null, 1));
    }

    public Task<RemoteChangePage> ChangesAsync(long cursor, CancellationToken ct) => Task.FromResult(new RemoteChangePage([], cursor, false));
    public Task DownloadAsync(string itemId, Stream destination, CancellationToken ct) => destination.WriteAsync(Data, ct).AsTask();
    public Task ReadRangeAsync(string itemId, long offset, long length, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken ct) =>
        sink(Data.AsMemory((int)offset, (int)length), ct).AsTask();
    public Task<RemoteItem> UploadNewAsync(string rootId, string? parentId, string name, Stream content, string hash, CancellationToken ct) => throw new NotSupportedException();
    public Task<RemoteItem> UploadVersionAsync(string itemId, long baseRevision, Stream content, string hash, CancellationToken ct) => throw new NotSupportedException();
    public Task<RemoteItem> CreateFolderAsync(string rootId, string? parentId, string name, CancellationToken ct) => throw new NotSupportedException();
    public Task<RemoteItem> MoveAsync(string itemId, long revision, string rootId, string? newParentId, string newName, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(string itemId, long revision, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Time spent in the Cloud Files calls (the rest of a pass is the engine, SQLite and the folder scan).</summary>
internal sealed class TimedPlaceholders(IPlaceholderProvider inner) : IPlaceholderProvider
{
    public readonly Stopwatch Create = new(), InSync = new();
    public bool OnDemand => inner.OnDemand;

    public async Task CreatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct)
    {
        Create.Start();
        try { await inner.CreatePlaceholderAsync(path, item, ct); }
        finally { Create.Stop(); }
    }

    public Task UpdatePlaceholderAsync(string path, RemoteItem item, CancellationToken ct) => inner.UpdatePlaceholderAsync(path, item, ct);

    public async Task MarkInSyncAsync(string path, string remoteId, CancellationToken ct)
    {
        InSync.Start();
        try { await inner.MarkInSyncAsync(path, remoteId, ct); }
        finally { InSync.Stop(); }
    }
}

internal static class Scale
{
    public static async Task RunAsync(int files, int folders, Dictionary<string, object> results)
    {
        // Under the user profile: Windows ignores sync roots in %TEMP%.
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BlueDrive-perf", Guid.NewGuid().ToString("N")[..8]);
        var root = Path.Combine(dir, "BlueDrive");
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(dir, "state.db");
        var remote = new BigRemote(files, folders);
        var account = Guid.NewGuid();
        Console.WriteLine($"## Quy mô: {files:N0} tệp / {folders:N0} thư mục (Tệp theo yêu cầu, Cloud Files API thật)\n");
        var baseline = Measure.WorkingSetMb();
        try
        {
            using var db = new SyncStateDb(dbPath);
            db.UpsertRoot(BigRemote.Root, "", true);
            var platform = new CloudFilesPlatform(new OnDemandContext(root, account, "Đo hiệu năng", new Uri("https://bluedrive.example/"), remote, dbPath));
            await platform.StartAsync(CancellationToken.None);
            try
            {
                var timed = new TimedPlaceholders(platform.Placeholders);
                var engine = new SyncEngine(db, remote, platform.FileSystem, SystemClock.Instance,
                    new SyncEngineOptions { UserDisplayName = "Đo hiệu năng", DeviceName = "PERF" }, timed);
                var sw = Stopwatch.StartNew();
                var first = await engine.SyncAsync(CancellationToken.None);
                var firstTime = sw.Elapsed;
                if (first.Errors.Count > 0) Console.WriteLine($"Lỗi: {string.Join("; ", first.Errors.Take(5))}");
                Console.WriteLine($"(trong đó Cloud Files: tạo tệp giữ chỗ {timed.Create.Elapsed.TotalSeconds:0.0} giây, đánh dấu đã đồng bộ {timed.InSync.Elapsed.TotalSeconds:0.0} giây)\n");
                var onDisk = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Count();
                sw.Restart();
                var second = await engine.SyncAsync(CancellationToken.None);
                var secondTime = sw.Elapsed;
                Memory.TrimAfter(first.Operations); // what the agent does after a large pass
                var ram = Measure.WorkingSetMb();
                var managed = Measure.ManagedMb();
                var cpu = await Measure.IdleCpuAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine($"(heap .NET còn giữ: {managed} MB, trong đó dữ liệu máy chủ giả lập của công cụ đo ~{remote.Count * 220 / 1024 / 1024} MB)\n");

                Console.WriteLine("| Chỉ số | Mục tiêu | Đo được |\n|---|---|---|");
                Console.WriteLine($"| Tạo tệp giữ chỗ lần đầu ({onDisk:N0} mục) | < 5 phút | {firstTime.TotalSeconds:0.0} giây |");
                Console.WriteLine($"| Quét khởi động, không thay đổi | < 30 giây | {secondTime.TotalSeconds:0.0} giây ({second.Errors.Count} lỗi) |");
                Console.WriteLine($"| RAM sau khi đồng bộ (working set) | < 200 MB | {ram} MB (nền trước khi đo {baseline} MB) |");
                Console.WriteLine($"| CPU khi rảnh (30 giây) | ≈ 0% | {cpu}% một lõi |");
                results["scale"] = new { files, folders, entriesOnDisk = onDisk, firstPassSeconds = Math.Round(firstTime.TotalSeconds, 1),
                    unchangedPassSeconds = Math.Round(secondTime.TotalSeconds, 1), workingSetMb = ram, idleCpuPercent = cpu };
            }
            finally
            {
                await platform.UnregisterAsync();
                await platform.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"Không xóa hết {dir}: {ex.Message}"); }
        }
    }
}

internal static class Latency
{
    private const string Redirect = "http://127.0.0.1:53999/callback";

    public static async Task RunAsync(Uri server, string user, string password, int rounds, Dictionary<string, object> results)
    {
        using var api = new HttpClient { BaseAddress = server };
        var login = await (await api.PostAsJsonAsync("api/v1/auth/login", new { username = user, password })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        var me = await api.GetFromJsonAsync<JsonElement>("api/v1/auth/me");
        var ws = me.GetProperty("personalWorkspaceId").GetGuid();

        // Device sign-in the way the app does it (PKCE), approved with the user's web session.
        var pkce = DesktopAuthenticator.PkcePair.Create();
        var authorize = await (await api.PostAsJsonAsync("api/v1/connect/authorize", new
        {
            clientId = DesktopAuthenticator.ClientId, redirectUri = Redirect, codeChallenge = pkce.Challenge, codeChallengeMethod = "S256", state = "perf", deviceName = "PERF",
        })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(authorize.GetProperty("redirectUrl").GetString()!).Query)["code"]!;
        var tokens = new MemoryTokenStore();
        tokens.Save(await new DesktopAuthenticator(server).ExchangeCodeAsync(code, pkce.Verifier, Redirect,
            new DeviceInfo("PERF-" + Environment.MachineName, "Windows", "perf", Guid.NewGuid().ToString())));

        var dir = Path.Combine(Path.GetTempPath(), "bluedrive-perf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Console.WriteLine($"## Độ trễ trên máy chủ thật ({server}, SignalR, chế độ đồng bộ đầy đủ)\n");
        var created = new List<(string Type, Guid Id)>();
        try
        {
            await using var agent = new SyncAgent(new AgentOptions
            {
                Server = server, SyncFolder = Path.Combine(dir, "BlueDrive"), StateDbPath = Path.Combine(dir, "state.db"), Tokens = tokens,
                DeviceName = "PERF", UseRecycleBin = false,
            });
            var sw = Stopwatch.StartNew();
            await agent.StartAsync();
            await Until(() => agent.Status.State == AgentState.Idle && agent.Status.LastSyncAt is not null, TimeSpan.FromMinutes(10));
            Console.WriteLine($"Đồng bộ ban đầu: {sw.Elapsed.TotalSeconds:0.0} giây\n");
            var personal = (await new HttpRemoteStore(new HttpClient(new DeviceAuthHandler(tokens, new DesktopAuthenticator(server)) { InnerHandler = new HttpClientHandler() }) { BaseAddress = server })
                .RootsAsync(CancellationToken.None)).First(r => r.Kind == "personal").RootId;
            var local = agent.LocalPathOf(personal, "");

            var webToPc = new List<double>();
            var pcToWeb = new List<double>();
            for (var i = 0; i < rounds; i++)
            {
                // Web → máy tính: a folder created on the web appears in the sync folder.
                var name = $"perf-web-{Guid.NewGuid():N}"[..20];
                sw.Restart();
                var folder = await (await api.PostAsJsonAsync("api/v1/folders", new { workspaceId = ws, name })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
                created.Add(("folder", folder.GetProperty("id").GetGuid()));
                await Until(() => Directory.Exists(Path.Combine(local, name)), TimeSpan.FromSeconds(60));
                webToPc.Add(sw.Elapsed.TotalSeconds);

                // Máy tính → máy chủ: an Office file saved in the sync folder (2 MB) is on the server.
                var file = $"perf-pc-{Guid.NewGuid():N}"[..19] + ".docx";
                var bytes = Docx(2 * 1024 * 1024);
                sw.Restart();
                await File.WriteAllBytesAsync(Path.Combine(local, file), bytes);
                Guid? id = null;
                await UntilAsync(async () =>
                {
                    var list = await api.GetFromJsonAsync<JsonElement>($"api/v1/workspaces/{ws}/items?q={Uri.EscapeDataString(file)}&pageSize=5");
                    var hit = list.GetProperty("items").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == file);
                    if (hit.ValueKind == JsonValueKind.Object) id = hit.GetProperty("id").GetGuid();
                    return id is not null;
                }, TimeSpan.FromSeconds(60));
                pcToWeb.Add(sw.Elapsed.TotalSeconds);
                created.Add(("file", id!.Value));
                Console.WriteLine($"Lượt {i + 1}: web → máy {webToPc[^1]:0.00} giây, lưu trên máy → máy chủ {pcToWeb[^1]:0.00} giây");
            }
            var ram = Measure.WorkingSetMb();
            var cpu = await Measure.IdleCpuAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine("\n| Chỉ số | Mục tiêu | Trung vị | p95 | Lớn nhất |\n|---|---|---|---|---|");
            Console.WriteLine($"| Thay đổi trên web xuất hiện trên máy | < 5 giây | {Measure.P(webToPc, 0.5)} | {Measure.P(webToPc, 0.95)} | {webToPc.Max():0.00} |");
            Console.WriteLine($"| Lưu tệp Office 2 MB trên máy → có trên máy chủ | < 10 giây | {Measure.P(pcToWeb, 0.5)} | {Measure.P(pcToWeb, 0.95)} | {pcToWeb.Max():0.00} |");
            Console.WriteLine($"\nRAM của tác tử đồng bộ: {ram} MB; CPU khi rảnh (60 giây): {cpu}% một lõi.");
            results["latency"] = new { server = server.ToString(), rounds, webToPcSeconds = webToPc, pcToServerSeconds = pcToWeb, workingSetMb = ram, idleCpuPercent = cpu };
        }
        finally
        {
            if (created.Count > 0)
            {
                using var del = await api.PostAsJsonAsync("api/v1/items/delete", new { items = created.Select(c => new { type = c.Type, id = c.Id }) });
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>A valid Word document of about <paramref name="size"/> bytes (the server checks Office content): a minimal package plus a picture.</summary>
    public static byte[] Docx(int size)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="bin" ContentType="application/octet-stream"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            Add("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            Add("word/document.xml", """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Đo hiệu năng BlueDrive</w:t></w:r></w:p></w:body></w:document>""");
            using var media = zip.CreateEntry("word/media/data.bin", System.IO.Compression.CompressionLevel.NoCompression).Open();
            media.Write(RandomNumberGenerator.GetBytes(size));
        }
        return ms.ToArray();
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException();
            await Task.Delay(200);
        }
    }
}
