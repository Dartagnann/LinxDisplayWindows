using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LinxDisplay.Core;

public sealed class KeyboardEndpointDiscovery : IDisposable
{
    private static readonly TimeSpan DefaultDiscoveryWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultScanDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(750);
    private const int MaximumParallelProbes = 32;

    private readonly HttpClient _client;
    private readonly Func<IReadOnlyList<IPAddress>> _candidateProvider;
    private readonly TimeSpan _discoveryWindow;
    private readonly TimeSpan _scanDelay;
    private readonly TimeSpan _probeTimeout;

    public KeyboardEndpointDiscovery()
        : this(new HttpClient(), GetLocalSubnetCandidates, DefaultDiscoveryWindow,
            DefaultScanDelay, DefaultProbeTimeout)
    {
    }

    internal KeyboardEndpointDiscovery(HttpMessageHandler handler,
        Func<IReadOnlyList<IPAddress>> candidateProvider,
        TimeSpan discoveryWindow, TimeSpan scanDelay, TimeSpan probeTimeout)
        : this(new HttpClient(handler), candidateProvider, discoveryWindow, scanDelay, probeTimeout)
    {
    }

    private KeyboardEndpointDiscovery(HttpClient client,
        Func<IReadOnlyList<IPAddress>> candidateProvider,
        TimeSpan discoveryWindow, TimeSpan scanDelay, TimeSpan probeTimeout)
    {
        _client = client;
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _candidateProvider = candidateProvider;
        _discoveryWindow = discoveryWindow;
        _scanDelay = scanDelay;
        _probeTimeout = probeTimeout;
    }

    public async Task<string?> ResolveAsync(string endpoint,
        CancellationToken cancellationToken = default, IProgress<int>? scanProgress = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var configuredUri)
            || (configuredUri.Scheme != Uri.UriSchemeHttp
                && configuredUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("图像 API 地址无效。");

        if (await IsKeyboardApiAsync(configuredUri, cancellationToken))
            return endpoint;

        var deadline = DateTimeOffset.UtcNow + _discoveryWindow;
        var scanCount = 0;
        while (true)
        {
            scanProgress?.Report(++scanCount);
            var discovered = await ScanAsync(configuredUri, cancellationToken);
            if (discovered is not null)
                return discovered.AbsoluteUri;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return null;

            await Task.Delay(remaining < _scanDelay ? remaining : _scanDelay, cancellationToken);
        }
    }

    private async Task<Uri?> ScanAsync(Uri configuredUri, CancellationToken cancellationToken)
    {
        var candidates = _candidateProvider()
            .Where(address => !string.Equals(address.ToString(), configuredUri.Host,
                StringComparison.OrdinalIgnoreCase))
            .Select(address => new UriBuilder(configuredUri) { Host = address.ToString() }.Uri)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(MaximumParallelProbes);
        var tasks = candidates.Select(async candidate =>
        {
            try
            {
                await gate.WaitAsync(scanCancellation.Token);
                try
                {
                    return await IsKeyboardApiAsync(candidate, scanCancellation.Token)
                        ? candidate
                        : null;
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }).ToList();

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);
            var discovered = await completed;
            if (discovered is null) continue;

            scanCancellation.Cancel();
            await Task.WhenAll(tasks);
            return discovered;
        }

        return null;
    }

    private async Task<bool> IsKeyboardApiAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_probeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return response.StatusCode == HttpStatusCode.MethodNotAllowed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static IReadOnlyList<IPAddress> GetLocalSubnetCandidates()
    {
        var subnets = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                              && network.NetworkInterfaceType != NetworkInterfaceType.Loopback
                              && network.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                              && network.NetworkInterfaceType != NetworkInterfaceType.Ppp)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                              && !IPAddress.IsLoopback(address)
                              && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.GetAddressBytes())
            .Where(bytes => bytes.Length == 4)
            .Select(bytes => (bytes[0], bytes[1], bytes[2]))
            .Distinct()
            .ToArray();

        return subnets
            .SelectMany(subnet => Enumerable.Range(1, 254)
                .Select(host => new IPAddress([subnet.Item1, subnet.Item2, subnet.Item3, (byte)host])))
            .ToArray();
    }

    public void Dispose() => _client.Dispose();
}
