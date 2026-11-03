using System.Text;
using BlueDrive.Sync.Agent;
using BlueDrive.Sync.Client;
using BlueDrive.Sync.Windows;

// bluedrive-sync: BlueDrive Desktop without the tray UI (full sync mode). Used for the Sync-Phase 4 demo and headless deployments.
Console.OutputEncoding = Encoding.UTF8;
var store = new AccountStore(Environment.GetEnvironmentVariable("BLUEDRIVE_HOME"));
var cmd = args.FirstOrDefault()?.ToLowerInvariant();
try
{
    return cmd switch
    {
        "login" => await LoginAsync(args.Skip(1).ToArray()),
        "run" => await RunAsync(),
        "status" => Status(),
        "logout" => Logout(args.Skip(1).ToArray()),
        _ => Help(),
    };
}
catch (SignInRequiredException ex)
{
    Console.Error.WriteLine($"Cần đăng nhập lại: {ex.Message}");
    return 2;
}

int Help()
{
    Console.WriteLine("""
        BlueDrive Desktop - đồng bộ tư liệu xuống máy tính (chế độ đồng bộ đầy đủ)

        Cách dùng:
          bluedrive-sync login <địa chỉ máy chủ> [--folder <thư mục>] [--full]
                                                                        Đăng nhập bằng trình duyệt, thêm tài khoản. Mặc định
                                                                        "Tệp theo yêu cầu" (chỉ tải khi mở); --full: tải hết về máy
          bluedrive-sync run                                            Chạy đồng bộ cho mọi tài khoản (Ctrl+C để dừng)
          bluedrive-sync status                                         Xem các tài khoản và trạng thái gần nhất
          bluedrive-sync logout <địa chỉ máy chủ>                       Gỡ tài khoản (tệp trên máy được giữ nguyên)

        Ví dụ:
          bluedrive-sync login https://bluedrive.bluestar.com.vn
        """);
    return cmd is null or "help" or "--help" or "-h" ? 0 : 1;
}

async Task<int> LoginAsync(string[] a)
{
    if (a.Length == 0 || !Uri.TryCreate(a[0], UriKind.Absolute, out var server) || server.Scheme is not ("https" or "http"))
    {
        Console.Error.WriteLine("Hãy nhập địa chỉ máy chủ BlueDrive, ví dụ: https://bluedrive.bluestar.com.vn");
        return 1;
    }
    if (server.Scheme == "http" && !server.IsLoopback)
    {
        Console.Error.WriteLine("Máy chủ phải dùng HTTPS (chỉ máy chủ thử nghiệm trên máy này mới được dùng HTTP).");
        return 1;
    }
    var folderArg = Array.IndexOf(a, "--folder") is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    var auth = new DesktopAuthenticator(BlueDriveHttp.Normalize(server));
    Console.WriteLine("Đang mở trình duyệt để đăng nhập BlueDrive… (đóng thẻ trình duyệt sau khi đăng nhập xong)");
    var tokens = await auth.SignInAsync(DeviceInfo.Current(Version()), u => OpenBrowser(u, Console.Out));

    var id = Guid.NewGuid();
    var tokenStore = store.Tokens(id);
    tokenStore.Save(tokens);
    using var http = BlueDriveHttp.Create(server, tokenStore, auth);
    var me = await new HttpRemoteStore(http).MeAsync(CancellationToken.None);
    var existing = store.Load();
    if (existing.FirstOrDefault(x => SameServer(x.ServerUrl, server) && x.Username == me.Username) is { } old)
    {
        // Signing in again: keep the account (folder, state) and replace its tokens.
        store.Tokens(id).Clear();
        store.Tokens(old.Id).Save(tokens);
        store.Save(old with { DeviceId = tokens.DeviceId });
        Console.WriteLine($"Đã đăng nhập lại {me.FullName} ({me.Username}). Thư mục: {old.SyncFolder}");
        return 0;
    }
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var folder = Path.GetFullPath(folderArg ?? (existing.Count == 0 ? Path.Combine(home, "BlueDrive") : Path.Combine(home, $"BlueDrive - {server.Host}")));
    if (existing.Any(x => string.Equals(Path.GetFullPath(x.SyncFolder), folder, StringComparison.OrdinalIgnoreCase)))
    {
        store.Tokens(id).Clear();
        Console.Error.WriteLine($"Thư mục {folder} đã được một tài khoản khác dùng. Hãy chọn thư mục khác bằng --folder.");
        return 1;
    }
    var onDemand = !a.Contains("--full") && SupportsOnDemand(folder);
    store.Save(new AccountConfig(id, BlueDriveHttp.Normalize(server).ToString(), folder, me.Username, me.FullName, tokens.DeviceId) { OnDemand = onDemand });
    Console.WriteLine($"Đã đăng nhập {me.FullName} ({me.Username}). Tư liệu sẽ được đồng bộ vào: {folder}");
    Console.WriteLine(onDemand
        ? "Chế độ: Tệp theo yêu cầu — tệp hiện đủ trong Explorer, chỉ tải về khi mở (chuột phải → \"Luôn giữ trên thiết bị\" để tải sẵn)."
        : "Chế độ: Đồng bộ đầy đủ — mọi tệp được tải về máy.");
    Console.WriteLine("Chạy \"bluedrive-sync run\" để bắt đầu đồng bộ.");
    return 0;
}

async Task<int> RunAsync()
{
    var accounts = store.Load();
    if (accounts.Count == 0)
    {
        Console.Error.WriteLine("Chưa có tài khoản nào. Chạy \"bluedrive-sync login <địa chỉ máy chủ>\" trước.");
        return 1;
    }
    using var single = new Mutex(true, @"Local\BlueDrive.Desktop.Agent", out var first);
    if (!first)
    {
        Console.Error.WriteLine("BlueDrive Desktop đang chạy trên máy này.");
        return 1;
    }
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    var agents = new List<SyncAgent>();
    var console = new object();
    void Print(AccountConfig acc, string text)
    {
        lock (console) Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{acc.Username}@{new Uri(acc.ServerUrl).Host}] {text}");
    }
    foreach (var acc in accounts.Where(a => !a.Paused))
    {
        var agent = new SyncAgent(new AgentOptions
        {
            Server = new Uri(acc.ServerUrl), SyncFolder = acc.SyncFolder, StateDbPath = store.StateDbPath(acc.Id), Tokens = store.Tokens(acc.Id),
            UserDisplayName = acc.DisplayName, AppVersion = Version(), AccountId = acc.Id,
            OnDemand = acc.OnDemand && SupportsOnDemand(acc.SyncFolder) ? ctx => Platform(ctx, acc, Print) : null,
        });
        agent.StatusChanged += s =>
        {
            Print(acc, s.Message);
            foreach (var p in s.Problems) Print(acc, "  • " + p);
        };
        agent.Activity += (action, path) => Print(acc, $"{action}: {path}");
        await agent.StartAsync(stop.Token);
        agents.Add(agent);
        Print(acc, $"Đang đồng bộ {acc.SyncFolder}");
    }
    Console.WriteLine("Nhấn Ctrl+C để dừng.");
    try { await Task.Delay(Timeout.Infinite, stop.Token); }
    catch (OperationCanceledException) { }
    Console.WriteLine("Đang dừng…");
    foreach (var a in agents) await a.DisposeAsync();
    return 0;
}

int Status()
{
    var accounts = store.Load();
    if (accounts.Count == 0)
    {
        Console.WriteLine("Chưa có tài khoản nào.");
        return 0;
    }
    foreach (var acc in accounts)
    {
        Console.WriteLine($"{acc.DisplayName} ({acc.Username}) — {acc.ServerUrl}");
        Console.WriteLine($"  Thư mục: {acc.SyncFolder}{(acc.Paused ? " (tạm dừng)" : "")}");
        Console.WriteLine($"  Đăng nhập: {(store.Tokens(acc.Id).Load() is null ? "cần đăng nhập lại" : "còn hiệu lực")}");
        if (!File.Exists(store.StateDbPath(acc.Id))) continue;
        using var db = new BlueDrive.Sync.Core.SyncStateDb(store.StateDbPath(acc.Id));
        var items = db.Roots().Sum(r => db.Items(r.RootId).Count);
        Console.WriteLine($"  Đã đồng bộ: {items} mục trong {db.Roots().Count(r => r.Enabled)} thư mục gốc; xung đột chưa xử lý: {db.Conflicts().Count}");
        foreach (var e in db.Activity(5)) Console.WriteLine($"    {e.At.ToLocalTime():dd/MM HH:mm} {e.Action}: {e.Path}");
    }
    return 0;
}

int Logout(string[] a)
{
    var accounts = store.Load();
    var acc = a.Length > 0 && Uri.TryCreate(a[0], UriKind.Absolute, out var server) ? accounts.FirstOrDefault(x => SameServer(x.ServerUrl, server))
        : accounts.Count == 1 ? accounts[0] : null;
    if (acc is null)
    {
        Console.Error.WriteLine("Không tìm thấy tài khoản. Hãy ghi rõ địa chỉ máy chủ.");
        return 1;
    }
    if (acc.OnDemand && OperatingSystem.IsWindows())
        SyncRootRegistrar.Unregister(new SyncRootInfo(acc.SyncFolder, acc.Id, "", Version(), "", null));
    store.Remove(acc.Id);
    Console.WriteLine($"Đã gỡ tài khoản {acc.Username}. Các tệp trong {acc.SyncFolder} được giữ nguyên trên máy.");
    return 0;
}

static CloudFilesPlatform Platform(OnDemandContext ctx, AccountConfig acc, Action<AccountConfig, string> print)
{
    var p = new CloudFilesPlatform(ctx, Environment.ProcessPath is { } exe ? $"{exe},0" : null, Version());
    p.Problem += (path, message) => print(acc, $"{path}: {message}");
    return p;
}

/// <summary>Files On-Demand needs Windows 10 1809+ and an NTFS volume (not FAT32 / exFAT / network drives: full sync there, spec 7.3).</summary>
static bool SupportsOnDemand(string folder)
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return false;
    try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).DriveFormat == "NTFS"; }
    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
}

static bool SameServer(string a, Uri b) => string.Equals(new Uri(a).GetLeftPart(UriPartial.Authority), b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

static string Version() => typeof(SyncAgent).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

static void OpenBrowser(Uri url, TextWriter output)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.ToString()) { UseShellExecute = true }); }
    catch (System.ComponentModel.Win32Exception) { }
    output.WriteLine($"Nếu trình duyệt không tự mở, hãy mở địa chỉ sau: {url}");
}
