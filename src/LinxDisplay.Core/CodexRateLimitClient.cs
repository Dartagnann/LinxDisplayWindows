using System.Diagnostics;
using System.Text.Json;

namespace LinxDisplay.Core;

public sealed class CodexRateLimitClient
{
    public async Task<UsageSnapshot> FetchAsync(
        string? configuredExecutable = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var process = StartCodex(configuredExecutable);
        try
        {
            await SendAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "codex_linx_display",
                        title = "LinxDisplay",
                        version = "0.5.0-preview"
                    },
                    capabilities = new { experimentalApi = true }
                }
            }, timeout.Token);

            var didRequestRateLimits = false;
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                        ? "Codex 数据连接意外关闭。"
                        : $"Codex 启动失败：{error.Trim()}");
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryGetId(root, out var id)) continue;

                if (id == 0 && !didRequestRateLimits)
                {
                    ThrowIfServerError(root);
                    didRequestRateLimits = true;
                    await SendAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
                    await SendAsync(process,
                        new { method = "account/rateLimits/read", id = 1, @params = (object?)null },
                        timeout.Token);
                    continue;
                }

                if (id != 1) continue;
                ThrowIfServerError(root);
                if (!root.TryGetProperty("result", out var result))
                    throw new InvalidOperationException("Codex 返回了无法识别的用量数据。");
                return ParseSnapshot(result);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取 Codex 用量超时。");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { }
            }
        }
    }

    private static Process StartCodex(string? configuredExecutable)
    {
        var executable = CodexCliLocator.Resolve(configuredExecutable);
        var isCommandScript = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                              || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isCommandScript)
            startInfo.Arguments = $"/d /s /c \"\"{executable}\" app-server --stdio\"";
        else
        {
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
        }

        try
        {
            return Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Codex 进程未能启动。");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Codex 启动失败：{error.Message}。请检查应用中的“Codex CLI”路径或 CODEX_CLI_PATH。", error);
        }
    }

    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static bool TryGetId(JsonElement root, out int id)
    {
        id = default;
        return root.TryGetProperty("id", out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out id);
    }

    private static void ThrowIfServerError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null) return;
        var message = error.TryGetProperty("message", out var value) ? value.GetString() : null;
        throw new InvalidOperationException($"Codex 返回错误：{message ?? "未知错误"}");
    }

    internal static UsageSnapshot ParseSnapshot(JsonElement result)
    {
        JsonElement limits = default;
        var hasLimits = result.TryGetProperty("rateLimitsByLimitId", out var byId)
                        && byId.ValueKind == JsonValueKind.Object
                        && byId.TryGetProperty("codex", out limits);
        if (!hasLimits)
            hasLimits = result.TryGetProperty("rateLimits", out limits)
                        && limits.ValueKind == JsonValueKind.Object;
        if (!hasLimits) throw new InvalidOperationException("Codex 返回了无法识别的用量数据。");

        var windows = new List<JsonElement>();
        if (limits.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
            windows.Add(primary);
        if (limits.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
            windows.Add(secondary);
        if (windows.Count == 0) throw new InvalidOperationException("Codex 返回了无法识别的用量数据。");

        var parsedWindows = windows.Select(ParseWindow).Where(window => window is not null)
            .Cast<UsageWindow>().ToArray();
        if (parsedWindows.Length == 0)
            throw new InvalidOperationException("Codex 返回的用量数据缺少 usedPercent。");
        var fiveHourWindow = parsedWindows.FirstOrDefault(window => window.WindowMinutes == 300);
        var weeklyWindow = parsedWindows.FirstOrDefault(window => window.WindowMinutes >= 10_080);
        var selected = fiveHourWindow ?? parsedWindows.MaxBy(window => window.WindowMinutes)!;

        var resetCount = 0;
        if (result.TryGetProperty("rateLimitResetCredits", out var credits)
            && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("availableCount", out var count)
            && count.TryGetInt32(out var parsedCount))
            resetCount = Math.Max(0, parsedCount);

        var planType = limits.TryGetProperty("planType", out var plan) ? plan.GetString() : null;
        return new UsageSnapshot(selected.RemainingPercent, selected.ResetDate,
            selected.WindowMinutes, resetCount, planType, fiveHourWindow, weeklyWindow);
    }

    private static UsageWindow? ParseWindow(JsonElement window)
    {
        if (!window.TryGetProperty("usedPercent", out var used)) return null;
        var windowMinutes = GetWindowMinutes(window);
        DateTimeOffset? resetDate = null;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.TryGetInt64(out var seconds))
            resetDate = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new UsageWindow(Math.Clamp(100 - (int)Math.Round(used.GetDouble()), 0, 100),
            resetDate, windowMinutes);
    }

    private static int GetWindowMinutes(JsonElement window) =>
        window.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt32(out var minutes)
            ? minutes : 0;
}
