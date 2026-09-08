using LinxDisplay.Core;
using LinxDisplay.Rendering;
using SkiaSharp;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

var checks = new List<(string Name, Action Run)>();
foreach (var theme in Enum.GetValues<CardTheme>())
{
    checks.Add(($"{theme} Codex weekly renderer", () => AssertJpeg(ScreenRenderer.RenderUsage(
        new UsageSnapshot(76, DateTimeOffset.Now.AddHours(3), 300, 4, "plus",
            new UsageWindow(76, DateTimeOffset.Now.AddHours(3), 300),
            new UsageWindow(91, DateTimeOffset.Now.AddDays(5), 10_080)),
        Settings(theme), new DateTimeOffset(2026, 7, 18, 2, 14, 0, TimeSpan.FromHours(8))))));
    checks.Add(($"{theme} Codex dual-window renderer", () => AssertJpeg(ScreenRenderer.RenderUsage(
        new UsageSnapshot(76, DateTimeOffset.Now.AddHours(3), 300, 4, "plus",
            new UsageWindow(76, DateTimeOffset.Now.AddHours(3), 300),
            new UsageWindow(91, DateTimeOffset.Now.AddDays(5), 10_080)),
        Settings(theme, DisplayMode.CodexDualWindow),
        new DateTimeOffset(2026, 7, 18, 2, 14, 0, TimeSpan.FromHours(8))))));
    checks.Add(($"{theme} Pomodoro renderer", () => AssertJpeg(ScreenRenderer.RenderPomodoro(
        new PomodoroSnapshot(PomodoroPhase.Focus, PomodoroPhase.Focus, "跨平台开发",
            TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(25), 3, DateTimeOffset.Now.AddMinutes(18)),
        Settings(theme)))));
    checks.Add(($"{theme} system renderer", () => AssertJpeg(ScreenRenderer.RenderSystem(
        new SystemSnapshot(42, 68, 11UL << 30, 16UL << 30, 2.5 * 1024 * 1024, 384 * 1024,
            TimeSpan.FromHours(53), DateTimeOffset.Now), Settings(theme)))));
}

checks.Add(("Codex 5-hour and weekly windows", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "planType": "plus",
            "primary": { "usedPercent": 35, "windowDurationMins": 10080, "resetsAt": 1786500000 },
            "secondary": { "usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1786000000 }
          },
          "rateLimitResetCredits": { "availableCount": 2 }
        }
        """);
    var snapshot = CodexRateLimitClient.ParseSnapshot(document.RootElement);
    Assert(snapshot.RemainingPercent == 80 && snapshot.WindowMinutes == 300,
        "没有优先显示恢复后的 5 小时窗口");
    Assert(snapshot.FiveHourWindow?.RemainingPercent == 80,
        "5 小时窗口解析错误");
    Assert(snapshot.WeeklyWindow?.RemainingPercent == 65,
        "周窗口解析错误");
    Assert(snapshot.AvailableResetCount == 2, "可用重置次数解析错误");
}));

checks.Add(("Pomodoro transitions", () =>
{
    var start = new DateTimeOffset(2026, 7, 18, 2, 14, 0, TimeSpan.FromHours(8));
    var service = new PomodoroService(new PomodoroState { FocusMinutes = 1, ShortBreakMinutes = 1 });
    service.StartOrResume(start);
    Assert(service.GetSnapshot(start).Phase == PomodoroPhase.Focus, "番茄钟未进入专注阶段");
    service.Tick(start.AddMinutes(1));
    var snapshot = service.GetSnapshot(start.AddMinutes(1));
    Assert(snapshot.Phase == PomodoroPhase.ShortBreak, "番茄钟未进入短休息");
    Assert(snapshot.CompletedFocusSessions == 1, "完成次数不正确");
}));

checks.Add(("Codex automatic sync schedule", () =>
{
    var now = new DateTimeOffset(2026, 7, 18, 2, 14, 30, TimeSpan.FromHours(8));
    Assert(AutomaticSyncPlanner.ForCodex(now, DateTimeOffset.MinValue, DateTimeOffset.MinValue, 300)
           == AutomaticSyncAction.RefreshAndPush, "首次启动没有安排刷新并推送");
    Assert(AutomaticSyncPlanner.ForCodex(now, now.AddMinutes(-5), now.AddSeconds(-10), 300)
           == AutomaticSyncAction.RefreshAndPush, "刷新周期到期没有安排刷新并推送");
    Assert(AutomaticSyncPlanner.ForCodex(now, now.AddMinutes(-1), now.AddMinutes(-1), 300)
           == AutomaticSyncAction.Push, "分钟变化没有安排时钟推送");
    Assert(AutomaticSyncPlanner.ForCodex(now, now.AddMinutes(-1), now.AddSeconds(-10), 300)
           == AutomaticSyncAction.None, "同一分钟内发生了重复推送");
}));

checks.Add(("macOS Codex CLI discovery paths", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "codex-cli-home");
    var configured = Path.Combine(home, "custom", "codex");
    var path = string.Join(Path.PathSeparator, Path.Combine(home, "bin"), "/usr/bin");
    var candidates = CodexCliLocator.BuildCandidatePaths(configured, null, path, home, false, true);
    Assert(candidates[0] == configured, "应用内指定的 Codex CLI 路径没有最高优先级");
    Assert(candidates.Contains(Path.Combine(home, "bin", "codex")), "没有检查继承的 PATH");
    Assert(candidates.Contains("/opt/homebrew/bin/codex"), "没有检查 Apple Silicon Homebrew 路径");
    Assert(candidates.Contains("/usr/local/bin/codex"), "没有检查 Intel Homebrew/npm 路径");
    Assert(candidates.Contains(Path.Combine(home, ".volta", "bin", "codex")), "没有检查 Volta 路径");
    Assert(candidates.Contains(Path.Combine(home, "Library", "pnpm", "codex")), "没有检查 macOS pnpm 路径");
}));

checks.Add(("Legacy application data migration", () =>
{
    var applicationData = Path.Combine(Path.GetTempPath(), $"linxdisplay-migration-{Guid.NewGuid():N}");
    var legacyDirectory = Path.Combine(applicationData, "CodexLinxDisplay");
    try
    {
        Directory.CreateDirectory(legacyDirectory);
        var legacyImagePath = Path.Combine(legacyDirectory, "custom-image.png");
        File.WriteAllBytes(legacyImagePath, [0x89, 0x50, 0x4e, 0x47]);
        File.WriteAllText(Path.Combine(legacyDirectory, "settings-v2.json"), JsonSerializer.Serialize(
            new AppSettings
            {
                Endpoint = "http://192.0.2.68/image/upload",
                CustomImagePath = legacyImagePath,
                CustomImageName = "migration.png"
            }));
        File.WriteAllText(Path.Combine(legacyDirectory, "pomodoro.json"), JsonSerializer.Serialize(
            new PomodoroState { TaskName = "Migration test", CompletedFocusSessions = 2 }));

        var store = new SettingsStore(applicationData);
        var settings = store.Load();
        Assert(settings.Endpoint == "http://192.0.2.68/image/upload", "旧 API 地址未迁移");
        Assert(settings.CustomImagePath == store.CustomImagePath, "自定义图片路径未切换到 LinxDisplay 目录");
        Assert(File.Exists(store.CustomImagePath), "自定义图片文件未迁移");
        Assert(store.LoadPomodoro().CompletedFocusSessions == 2, "番茄钟状态未迁移");
    }
    finally
    {
        if (Directory.Exists(applicationData)) Directory.Delete(applicationData, true);
    }
}));

checks.Add(("Platform system monitor", () =>
{
    var monitor = PlatformServiceFactory.CreateSystemMonitor();
    monitor.Sample();
    Thread.Sleep(50);
    var sample = monitor.Sample();
    Assert(sample.CpuPercent is >= 0 and <= 100, "CPU 百分比越界");
    Assert(sample.MemoryPercent is >= 0 and <= 100, "内存百分比越界");
    Assert(sample.TotalMemoryBytes > 0, "未读取到系统内存");
}));

checks.Add(("Image API retries a transient connection failure",
    () => TestTransientImageApiConnectionAsync().GetAwaiter().GetResult()));

checks.Add(("Keyboard API address is discovered and updated",
    () => TestKeyboardEndpointDiscoveryAsync().GetAwaiter().GetResult()));

var failures = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS  {check.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {check.Name}: {error}");
    }
}

Console.WriteLine($"{checks.Count - failures}/{checks.Count} checks passed");
return failures == 0 ? 0 : 1;

static AppSettings Settings(CardTheme theme, DisplayMode mode = DisplayMode.Codex) => new()
{
    CardTheme = theme,
    DisplayMode = mode,
    SafeAreaHeight = 56,
    JpegQuality = 90
};

static void AssertJpeg(byte[] data)
{
    Assert(data.Length is > 1000 and <= ScreenRenderer.MaximumFileSize, "JPEG 文件大小异常");
    Assert(data[0] == 0xff && data[1] == 0xd8, "不是 JPEG 数据");
    using var bitmap = SKBitmap.Decode(data);
    if (bitmap is null) throw new InvalidOperationException("JPEG 无法解码");
    Assert(bitmap.Width == ScreenRenderer.Width && bitmap.Height == ScreenRenderer.Height,
        $"图片尺寸错误：{bitmap.Width}×{bitmap.Height}");
}

static async Task TestTransientImageApiConnectionAsync()
{
    using var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    using var listener = new TcpListener(IPAddress.Loopback, port);

    var server = Task.Run(async () =>
    {
        await Task.Delay(750);
        listener.Start();
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        await stream.ReadAtLeastAsync(buffer, 1);
        await stream.WriteAsync(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray());
    });

    using var api = new ImageApiClient();
    var retryCount = 0;
    var retryProgress = new Progress<int>(_ => Interlocked.Increment(ref retryCount));
    var status = await api.UploadAsync([0xff, 0xd8, 0xff, 0xd9],
        $"http://127.0.0.1:{port}/image/upload", connectionRetryProgress: retryProgress);
    await server;
    Assert(status == 200, "图像 API 短暂不可用后没有重试成功");
    Assert(retryCount > 0, "图像 API 短暂不可用时没有报告重试状态");
}

static async Task TestKeyboardEndpointDiscoveryAsync()
{
    var requests = new List<Uri>();
    using var discovery = new KeyboardEndpointDiscovery(
        new StubHttpMessageHandler(request =>
        {
            lock (requests) requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host is "198.18.0.18" or "10.0.0.54")
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            throw new HttpRequestException("Host unavailable");
        }),
        () =>
        [
            IPAddress.Parse("198.18.0.1"),
            IPAddress.Parse("10.0.0.53"),
            IPAddress.Parse("10.0.0.54")
        ],
        TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

    var resolved = await discovery.ResolveAsync("http://198.18.0.18/image/upload");
    Assert(resolved == "http://10.0.0.54/image/upload", "没有发现新的键盘 API 地址");
    Assert(requests.All(uri => !uri.Host.StartsWith("198.18.", StringComparison.Ordinal)),
        "不应探测代理使用的基准测试网段");
    Assert(requests.Any(uri => uri.Host == "10.0.0.54"), "没有扫描局域网候选地址");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
