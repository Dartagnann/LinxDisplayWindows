using System.Net.Http.Headers;

namespace LinxDisplay.Core;

public sealed class ImageApiClient : IDisposable
{
    private static readonly TimeSpan ConnectionRetryWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(5);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<int> UploadAsync(byte[] jpeg, string endpoint,
        CancellationToken cancellationToken = default, IProgress<int>? connectionRetryProgress = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("图像 API 地址无效。");

        var retryDeadline = DateTimeOffset.UtcNow + ConnectionRetryWindow;
        var retryCount = 0;
        while (true)
        {
            using var content = new ByteArrayContent(jpeg);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            try
            {
                using var response = await _client.PostAsync(uri, content, cancellationToken);
                var responseText = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(string.IsNullOrEmpty(responseText)
                        ? $"图像 API 返回 HTTP {(int)response.StatusCode}。"
                        : $"图像 API 返回 HTTP {(int)response.StatusCode}：{responseText}", null, response.StatusCode);
                return (int)response.StatusCode;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested
                                                && DateTimeOffset.UtcNow < retryDeadline)
            {
                connectionRetryProgress?.Report(++retryCount);
                await DelayBeforeRetryAsync(retryDeadline, cancellationToken);
            }
            catch (HttpRequestException error) when (error.StatusCode is null
                                                     && DateTimeOffset.UtcNow < retryDeadline)
            {
                connectionRetryProgress?.Report(++retryCount);
                await DelayBeforeRetryAsync(retryDeadline, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("图像 API 在 5 分钟内未恢复，请检查键盘网络或重启设备。");
            }
            catch (HttpRequestException error) when (error.StatusCode is null)
            {
                throw new HttpRequestException("图像 API 在 5 分钟内未恢复，请检查键盘网络或重启设备。", error);
            }
        }
    }

    private static async Task DelayBeforeRetryAsync(DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining < ConnectionRetryDelay ? remaining : ConnectionRetryDelay,
                cancellationToken);
    }

    public void Dispose() => _client.Dispose();
}
