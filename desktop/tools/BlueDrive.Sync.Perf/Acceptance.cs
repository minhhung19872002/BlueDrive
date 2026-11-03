using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;

/// <summary>
/// Section 15 acceptance run (spec "TIÊU CHÍ HOÀN THÀNH") against a real BlueDrive server with two independent BlueDrive Desktop agents
/// ("máy A", "máy B": separate device sign-ins, sync folders on the real disk, SignalR, full sync mode). Prints a Markdown report.
///   bluedrive-perf acceptance --server URL --user admin --password P
/// </summary>
internal static class Acceptance
{
    private const string Redirect = "http://127.0.0.1:53998/callback";
    private sealed record Row(string Criterion, bool Ok, string Measured);

    private sealed class Pc(string name, SyncAgent agent, Guid deviceId, string personal) : IAsyncDisposable
    {
        public string Name => name;
        public SyncAgent Agent => agent;
        public Guid DeviceId => deviceId;
        public string Personal => personal;
        public string P(string rel) => Path.Combine(personal, rel.Replace('/', Path.DirectorySeparatorChar));
        public ValueTask DisposeAsync() => agent.DisposeAsync();
    }

    public static async Task RunAsync(Uri server, string user, string password, Dictionary<string, object> results)
    {
        using var api = new HttpClient { BaseAddress = server };
        var token = (await (await api.PostAsJsonAsync("api/v1/auth/login", new { username = user, password })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var ws = (await api.GetFromJsonAsync<JsonElement>("api/v1/auth/me")).GetProperty("personalWorkspaceId").GetGuid();
        var dir = Path.Combine(Path.GetTempPath(), "bluedrive-acceptance-" + Guid.NewGuid().ToString("N")[..8]);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var top = $"Nghiệm thu {tag}";
        var rows = new List<Row>();
        Console.WriteLine($"## Nghiệm thu mục 15 — 2 thiết bị (máy A, máy B) trên máy chủ thật {server}\n");

        await using var a = await PcAsync(api, server, dir, "MAY-A");
        await using var b = await PcAsync(api, server, dir, "MAY-B");
        try
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(a.P(top));
            await UntilAsync(() => Task.FromResult(Directory.Exists(b.P(top))), 30);
            await Idle(a, b);

            // 1. Máy → web: thêm, sửa, đổi tên, di chuyển, xóa — mỗi thao tác dưới 10 giây.
            sw.Restart();
            await File.WriteAllBytesAsync(a.P($"{top}/kế hoạch.docx"), Latency.Docx(300_000));
            var id = await UntilServerAsync(api, ws, top, "kế hoạch.docx", 30);
            var tAdd = sw.Elapsed.TotalSeconds;
            sw.Restart();
            await File.WriteAllBytesAsync(a.P($"{top}/kế hoạch.docx"), Latency.Docx(310_000));
            await UntilAsync(async () => (await Versions(api, id)) >= 2, 30);
            var tEdit = sw.Elapsed.TotalSeconds;
            sw.Restart();
            File.Move(a.P($"{top}/kế hoạch.docx"), a.P($"{top}/kế hoạch 2026.docx"));
            await UntilAsync(async () => (await Item(api, id)).GetProperty("name").GetString() == "kế hoạch 2026.docx", 30);
            var tRename = sw.Elapsed.TotalSeconds;
            Directory.CreateDirectory(a.P($"{top}/Lưu trữ"));
            await UntilServerFolderAsync(api, ws, top, "Lưu trữ", 30);
            sw.Restart();
            File.Move(a.P($"{top}/kế hoạch 2026.docx"), a.P($"{top}/Lưu trữ/kế hoạch 2026.docx"));
            var folderId = await UntilServerFolderAsync(api, ws, top, "Lưu trữ", 30);
            await UntilAsync(async () => (await Item(api, id)).GetProperty("folderId").GetString() == folderId.ToString(), 30);
            var tMove = sw.Elapsed.TotalSeconds;
            sw.Restart();
            File.Delete(a.P($"{top}/Lưu trữ/kế hoạch 2026.docx"));
            await UntilAsync(async () => (await api.GetAsync($"api/v1/files/{id}")).StatusCode == System.Net.HttpStatusCode.NotFound, 30);
            var tDelete = sw.Elapsed.TotalSeconds;
            var pcToWeb = new[] { tAdd, tEdit, tRename, tMove, tDelete };
            rows.Add(new("Thêm / sửa / đổi tên / di chuyển / xóa trên máy → web dưới 10 giây", pcToWeb.All(t => t < 10),
                $"thêm {tAdd:0.0} s, sửa {tEdit:0.0} s, đổi tên {tRename:0.0} s, di chuyển {tMove:0.0} s, xóa {tDelete:0.0} s"));

            // 2. Web → máy dưới 5 giây (máy B).
            sw.Restart();
            var webFolder = $"Từ web {tag}";
            await (await api.PostAsJsonAsync("api/v1/folders", new { workspaceId = ws, name = webFolder })).Content.ReadFromJsonAsync<JsonElement>();
            await UntilAsync(() => Task.FromResult(Directory.Exists(b.P(webFolder))), 30);
            var tWeb = sw.Elapsed.TotalSeconds;
            rows.Add(new("Thay đổi trên web xuất hiện trên máy dưới 5 giây", tWeb < 5, $"{tWeb:0.00} s"));
            await Idle(a, b);

            // 3. Sửa đồng thời 2 nơi → bản xung đột đúng tên, không mất nội dung.
            await File.WriteAllTextAsync(a.P($"{top}/biên bản.txt"), "bản gốc");
            await UntilAsync(() => Task.FromResult(File.Exists(b.P($"{top}/biên bản.txt"))), 30);
            await Idle(a, b);
            a.Agent.Pause(true);
            b.Agent.Pause(true);
            await File.WriteAllTextAsync(a.P($"{top}/biên bản.txt"), "nội dung của máy A");
            await File.WriteAllTextAsync(b.P($"{top}/biên bản.txt"), "nội dung của máy B");
            a.Agent.Pause(false);
            await Idle(a);
            b.Agent.Pause(false);
            await Idle(b);
            await Idle(a, b);
            var copies = Directory.GetFiles(b.P(top), "biên bản (Bản xung đột -*").Concat(Directory.GetFiles(a.P(top), "biên bản (Bản xung đột -*")).ToList();
            var texts = Directory.GetFiles(b.P(top), "biên bản*").Select(File.ReadAllText).ToList();
            rows.Add(new("Sửa đồng thời 2 nơi tạo bản xung đột đúng định dạng tên, không mất nội dung",
                copies.Count >= 1 && texts.Contains("nội dung của máy A") && texts.Contains("nội dung của máy B"),
                copies.Count == 0 ? "không có bản xung đột" : Path.GetFileName(copies[0])));

            // 4. Ngoại tuyến (tạm dừng đồng bộ = không gửi gì), sửa 50 tệp, nối lại → lên máy chủ đủ.
            Directory.CreateDirectory(a.P($"{top}/Ngoại tuyến"));
            await Idle(a);
            a.Agent.Pause(true);
            for (var i = 0; i < 50; i++) await File.WriteAllTextAsync(a.P($"{top}/Ngoại tuyến/tệp {i:00}.txt"), $"nội dung {i}");
            await Task.Delay(3000);
            var whilePaused = await CountServerAsync(api, ws, top, "Ngoại tuyến");
            sw.Restart();
            a.Agent.Pause(false);
            await UntilAsync(async () => await CountServerAsync(api, ws, top, "Ngoại tuyến") == 50, 120);
            rows.Add(new("Mất mạng, sửa 50 tệp, có mạng lại: tất cả lên máy chủ đúng", whilePaused == 0,
                $"lúc ngoại tuyến trên máy chủ có {whilePaused} tệp; sau khi nối lại đủ 50 tệp trong {sw.Elapsed.TotalSeconds:0.0} s"));
            await Idle(a, b);

            // 5. Xóa hơn 100 tệp → hỏi xác nhận, máy chủ chưa xóa.
            Directory.CreateDirectory(a.P($"{top}/Xóa nhiều"));
            for (var i = 0; i < 120; i++) await File.WriteAllTextAsync(a.P($"{top}/Xóa nhiều/{i:000}.txt"), "x");
            await UntilAsync(async () => await CountServerAsync(api, ws, top, "Xóa nhiều") == 120, 180);
            await Idle(a, b);
            foreach (var f in Directory.GetFiles(a.P($"{top}/Xóa nhiều"))) File.Delete(f);
            try { await UntilAsync(() => Task.FromResult(a.Agent.Status.PendingMassDelete > 0), 60); }
            catch (TimeoutException)
            {
                Console.WriteLine($"[chẩn đoán] trạng thái máy A: {a.Agent.Status.State} \"{a.Agent.Status.Message}\" vấn đề: {string.Join("; ", a.Agent.Status.Problems.Take(3))}; " +
                                  $"máy chủ còn {await CountServerAsync(api, ws, top, "Xóa nhiều")} tệp");
                throw;
            }
            var stillOnServer = await CountServerAsync(api, ws, top, "Xóa nhiều");
            rows.Add(new("Xóa hơn 100 tệp thì ứng dụng hỏi xác nhận", a.Agent.Status.PendingMassDelete >= 100 && stillOnServer == 120,
                $"ứng dụng: \"{a.Agent.Status.Message}\"; máy chủ vẫn còn {stillOnServer} tệp"));
            a.Agent.ResolveMassDelete(deleteOnServer: false);
            await UntilAsync(() => Task.FromResult(Directory.GetFiles(a.P($"{top}/Xóa nhiều")).Length == 120), 120);

            // 6. Chia sẻ: liên kết có mật khẩu (lệnh "Chia sẻ…" của menu chuột phải).
            await File.WriteAllTextAsync(a.P($"{top}/chia sẻ.txt"), "tài liệu chia sẻ");
            var shareId = await UntilServerAsync(api, ws, top, "chia sẻ.txt", 30);
            var link = await a.Agent.Api.CreateLinkAsync(shareId.ToString(), allowDownload: true, password: "Mk@2026xyz", DateTime.UtcNow.AddDays(7), requireLogin: false);
            rows.Add(new("Menu chuột phải \"Chia sẻ…\" tạo được liên kết có mật khẩu", link.HasPassword && link.Url.Contains("/s/"), link.Url));

            // 7. Tệp chỉ có quyền Xem không bị tải xuống máy.
            var (viewName, dlName) = await ShareFromColleagueAsync(api, tag);
            await UntilAsync(() => Task.FromResult(Directory.EnumerateFiles(a.Agent.SyncFolder, dlName, SearchOption.AllDirectories).Any()), 60);
            var viewOnDisk = Directory.EnumerateFiles(a.Agent.SyncFolder, viewName, SearchOption.AllDirectories).Any();
            rows.Add(new("Tệp chỉ có quyền Xem không bị tải xuống máy", !viewOnDisk,
                $"tệp được chia sẻ quyền Xem + Tải xuống có trên máy, tệp chỉ Xem {(viewOnDisk ? "CÓ" : "không có")} trên máy"));

            // 8. Thu hồi thiết bị từ trang quản trị; nhật ký ghi device_id.
            var audit = await (await api.PostAsJsonAsync("api/v1/audit-logs/search?page=1&pageSize=200", new { q = top })).Content.ReadFromJsonAsync<JsonElement>();
            var withDevice = audit.GetProperty("items").EnumerateArray().Count(e => (e.GetProperty("detail").GetString() ?? "").Contains(a.DeviceId.ToString(), StringComparison.OrdinalIgnoreCase));
            sw.Restart();
            (await api.PostAsync($"api/v1/devices/{b.DeviceId}/revoke", null)).EnsureSuccessStatusCode();
            await File.WriteAllTextAsync(b.P($"{top}/sau thu hồi.txt"), "x"); // next request of B
            await UntilAsync(() => Task.FromResult(b.Agent.Status.State is AgentState.Revoked or AgentState.NeedsSignIn), 120);
            rows.Add(new("Thu hồi thiết bị từ trang quản trị hoạt động; nhật ký ghi đủ device_id",
                withDevice > 0 && b.Agent.Status.State is AgentState.Revoked or AgentState.NeedsSignIn,
                $"máy B dừng sau {sw.Elapsed.TotalSeconds:0.0} s (trạng thái {b.Agent.Status.State}); {withDevice} mục nhật ký có device_id của máy A"));
        }
        finally
        {
            await CleanupAsync(api, ws, top, $"Từ web {tag}");
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        Console.WriteLine("| Tiêu chí (mục 15) | Kết quả | Đo được |\n|---|---|---|");
        foreach (var r in rows) Console.WriteLine($"| {r.Criterion} | {(r.Ok ? "Đạt" : "**Không đạt**")} | {r.Measured} |");
        results["acceptance"] = rows.Select(r => new { r.Criterion, r.Ok, r.Measured }).ToList();
    }

    private static async Task<Pc> PcAsync(HttpClient api, Uri server, string dir, string name)
    {
        var pkce = DesktopAuthenticator.PkcePair.Create();
        var authorize = await (await api.PostAsJsonAsync("api/v1/connect/authorize", new
        {
            clientId = DesktopAuthenticator.ClientId, redirectUri = Redirect, codeChallenge = pkce.Challenge, codeChallengeMethod = "S256", state = name, deviceName = name,
        })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(authorize.GetProperty("redirectUrl").GetString()!).Query)["code"]!;
        var tokens = new MemoryTokenStore();
        var t = await new DesktopAuthenticator(server).ExchangeCodeAsync(code, pkce.Verifier, Redirect, new DeviceInfo(name, "Windows 11", "nghiem-thu", Guid.NewGuid().ToString()));
        tokens.Save(t);
        var agent = new SyncAgent(new AgentOptions
        {
            Server = server, SyncFolder = Path.Combine(dir, name, "BlueDrive"), StateDbPath = Path.Combine(dir, name, "state.db"), Tokens = tokens,
            DeviceName = name, UseRecycleBin = false,
        });
        await agent.StartAsync();
        await UntilAsync(() => Task.FromResult(agent.Status.State == AgentState.Idle && agent.Status.LastSyncAt is not null), 600);
        var personal = agent.Roots.First(r => r.Kind == "personal").RootId;
        return new Pc(name, agent, t.DeviceId, agent.LocalPathOf(personal, ""));
    }

    private static async Task Idle(params Pc[] pcs)
    {
        await Task.Delay(4000); // past the 3 s quiet time of local changes
        foreach (var p in pcs) await UntilAsync(() => Task.FromResult(p.Agent.Status.State == AgentState.Idle), 120);
    }

    private static async Task<JsonElement> Item(HttpClient api, Guid id) => await api.GetFromJsonAsync<JsonElement>($"api/v1/files/{id}");

    private static async Task<int> Versions(HttpClient api, Guid id) => (await api.GetFromJsonAsync<JsonElement>($"api/v1/files/{id}/versions")).GetArrayLength();

    private static async Task<Guid?> FolderIdAsync(HttpClient api, Guid ws, Guid? parent, string name)
    {
        var list = await api.GetFromJsonAsync<JsonElement>($"api/v1/workspaces/{ws}/items?pageSize=200&q={Uri.EscapeDataString(name)}" + (parent is { } p ? $"&folderId={p}" : ""));
        var hit = list.GetProperty("items").EnumerateArray().FirstOrDefault(i => i.GetProperty("name").GetString() == name && i.GetProperty("type").GetString() == "folder");
        return hit.ValueKind == JsonValueKind.Object ? hit.GetProperty("id").GetGuid() : null;
    }

    private static async Task<Guid> UntilServerFolderAsync(HttpClient api, Guid ws, string top, string name, int seconds)
    {
        Guid? id = null;
        await UntilAsync(async () => (id = await FolderIdAsync(api, ws, await FolderIdAsync(api, ws, null, top), name)) is not null, seconds);
        return id!.Value;
    }

    private static async Task<Guid> UntilServerAsync(HttpClient api, Guid ws, string top, string file, int seconds)
    {
        Guid? id = null;
        await UntilAsync(async () =>
        {
            var folder = await FolderIdAsync(api, ws, null, top);
            if (folder is null) return false;
            var list = await api.GetFromJsonAsync<JsonElement>($"api/v1/workspaces/{ws}/items?pageSize=200&folderId={folder}");
            var hit = list.GetProperty("items").EnumerateArray().FirstOrDefault(i => i.GetProperty("name").GetString() == file);
            if (hit.ValueKind == JsonValueKind.Object) id = hit.GetProperty("id").GetGuid();
            return id is not null;
        }, seconds);
        return id!.Value;
    }

    private static async Task<int> CountServerAsync(HttpClient api, Guid ws, string top, string sub)
    {
        var folder = await FolderIdAsync(api, ws, await FolderIdAsync(api, ws, null, top), sub);
        if (folder is null) return 0;
        return (await api.GetFromJsonAsync<JsonElement>($"api/v1/workspaces/{ws}/items?pageSize=1&folderId={folder}")).GetProperty("total").GetInt32();
    }

    /// <summary>A colleague (created for the run) shares one file view-only and one with download with the signed-in user.</summary>
    private static async Task<(string ViewOnly, string Download)> ShareFromColleagueAsync(HttpClient api, string tag)
    {
        var me = await api.GetFromJsonAsync<JsonElement>("api/v1/auth/me");
        var roles = await api.GetFromJsonAsync<JsonElement>("api/v1/roles");
        var editor = roles.EnumerateArray().First(r => r.GetProperty("code").GetString() == "bien_tap").GetProperty("id").GetGuid();
        var username = "nghiemthu_" + tag;
        await (await api.PostAsJsonAsync("api/v1/users", new { username, fullName = "Đồng nghiệp nghiệm thu", isActive = true, password = "Init@Pass2026", roleIds = new[] { editor } }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using var other = new HttpClient { BaseAddress = api.BaseAddress };
        var login = await (await other.PostAsJsonAsync("api/v1/auth/login", new { username, password = "Init@Pass2026" })).Content.ReadFromJsonAsync<JsonElement>();
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        var changed = await (await other.PostAsJsonAsync("api/v1/auth/change-password", new { currentPassword = "Init@Pass2026", newPassword = "Next@Pass2026", signOutOtherSessions = false }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.GetProperty("accessToken").GetString());
        var ws = (await other.GetFromJsonAsync<JsonElement>("api/v1/auth/me")).GetProperty("personalWorkspaceId").GetGuid();
        var names = (View: $"chỉ xem {tag}.txt", Download: $"được tải {tag}.txt");
        foreach (var (name, level) in new[] { (names.View, 1), (names.Download, 2) })
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("tài liệu của đồng nghiệp")), "file", name },
                { new StringContent(ws.ToString()), "workspaceId" },
            };
            var up = await (await other.PostAsync("api/v1/files/upload", form)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
            await (await other.PostAsJsonAsync("api/v1/shares", new
            {
                resourceType = "file", resourceId = up.GetProperty("fileId").GetGuid(), targets = new[] { new { type = "user", id = me.GetProperty("id").GetGuid() } }, accessLevel = level,
            })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        }
        return names;
    }

    private static async Task CleanupAsync(HttpClient api, Guid ws, params string[] tops)
    {
        var items = new List<object>();
        foreach (var t in tops)
            if (await FolderIdAsync(api, ws, null, t) is { } id) items.Add(new { type = "folder", id });
        if (items.Count > 0) using (await api.PostAsJsonAsync("api/v1/items/delete", new { items })) { }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"Quá {seconds} giây chờ điều kiện.");
            await Task.Delay(100);
        }
    }
}
