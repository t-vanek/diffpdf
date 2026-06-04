using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Configuration;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Finds a diffpdf server on the LAN when no explicit URL is configured: first probes <c>localhost</c>
/// (fast path for same-machine dev), then UDP-broadcasts a discovery token and takes the first server
/// that replies — host comes from the reply packet's source IP, scheme/port from the reply payload.
/// </summary>
public sealed class ServerDiscoveryClient
{
    /// <summary>Must match the server's <c>ServerDiscoveryResponder.Magic</c>.</summary>
    private const string Magic = "DIFFPDF-DISCOVER/1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves a reachable server base URL, or null if none is found.</summary>
    public async Task<Uri?> DiscoverAsync(DiscoveryConfig discovery, CancellationToken ct = default)
    {
        if (!discovery.Enabled)
            return null;

        // 1) Fast path: a server on this machine at the default port.
        var localhost = new Uri($"http://localhost:{discovery.DefaultPort}");
        if (await IsHealthyAsync(localhost, ct))
            return localhost;

        // 2) LAN: broadcast a probe and take the first valid reply.
        try
        {
            return await BroadcastAsync(discovery, ct);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<bool> IsHealthyAsync(Uri baseUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(1) };
            return await new DiffPdfClient(http).GetServerInfoAsync(ct) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Uri?> BroadcastAsync(DiscoveryConfig discovery, CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var probe = Encoding.ASCII.GetBytes(Magic);
        await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, discovery.Port), ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, discovery.TimeoutSeconds)));

        try
        {
            // Loop so a stray/garbage datagram doesn't abort discovery before the real reply or timeout.
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                if (TryParseReply(result, out var uri))
                    return uri;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out with no valid reply
        }
    }

    private static bool TryParseReply(UdpReceiveResult result, out Uri? uri)
    {
        uri = null;
        try
        {
            var reply = JsonSerializer.Deserialize<DiscoveryReply>(result.Buffer, JsonOptions);
            if (reply is null || reply.HttpPort <= 0)
                return false;

            var scheme = string.IsNullOrWhiteSpace(reply.Scheme) ? "http" : reply.Scheme;
            uri = new Uri($"{scheme}://{result.RemoteEndPoint.Address}:{reply.HttpPort}");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record DiscoveryReply(string Scheme, int HttpPort, string Version, bool AuthEnabled);
}
