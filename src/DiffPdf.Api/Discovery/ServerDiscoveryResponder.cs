using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DiffPdf.Api.Operational;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Discovery;

/// <summary>
/// Answers LAN discovery probes so the desktop client can auto-find this server without a hardcoded
/// address. Listens for a UDP broadcast carrying <see cref="Magic"/> on the configured port and replies
/// (unicast) with this server's HTTP scheme + port, build version, and whether auth is required; the
/// client derives the host from the reply packet's source IP. Not leader-gated — every replica answers,
/// so a client always finds a reachable server. A bind failure (e.g. a second instance on the same host)
/// is non-fatal: the responder logs a warning and goes idle.
/// </summary>
public sealed class ServerDiscoveryResponder(
    IServer server,
    ServerAuthInfo authInfo,
    IOptions<DiscoveryOptions> options,
    ILogger<ServerDiscoveryResponder> logger) : BackgroundService
{
    /// <summary>Probe token the client broadcasts; bump the suffix on any wire-format change.</summary>
    public const string Magic = "DIFFPDF-DISCOVER/1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private byte[]? _reply;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Port;

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Server discovery disabled: could not bind UDP port {Port} (already in use?).", port);
            return;
        }

        using (udp)
        {
            logger.LogInformation("Server discovery responder listening on UDP {Port}.", port);
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "Discovery receive failed; continuing.");
                    continue;
                }

                var probe = Encoding.ASCII.GetString(received.Buffer);
                if (!probe.StartsWith(Magic, StringComparison.Ordinal))
                    continue;

                var reply = BuildReply();
                if (reply is null)
                    continue;

                try
                {
                    await udp.SendAsync(reply, received.RemoteEndPoint, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Discovery reply to {Remote} failed.", received.RemoteEndPoint);
                }
            }
        }
    }

    /// <summary>Builds (and caches) the JSON reply describing this server's HTTP endpoint + auth state.
    /// Returns null until the server's listening address is known (probes simply retry).</summary>
    private byte[]? BuildReply()
    {
        if (_reply is not null)
            return _reply;

        if (!TryResolveHttpEndpoint(out var scheme, out var httpPort))
        {
            logger.LogWarning("Server discovery: could not determine the HTTP listening port yet; not replying.");
            return null;
        }

        _reply = JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryReply(scheme, httpPort, BuildInfo.Version, authInfo.AuthEnabled), JsonOptions);
        return _reply;
    }

    /// <summary>Reads the server's bound address, preferring HTTP (the desktop client uses a plain HttpClient).</summary>
    private bool TryResolveHttpEndpoint(out string scheme, out int port)
    {
        scheme = "http";
        port = 0;

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            return false;

        foreach (var preferred in new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps })
        {
            foreach (var raw in addresses)
            {
                // Wildcard hosts (http://*:5275, http://+:5275) aren't valid URIs — normalize before parsing.
                var normalized = raw.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0");
                if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme == preferred)
                {
                    scheme = preferred;
                    port = uri.Port;
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record DiscoveryReply(string Scheme, int HttpPort, string Version, bool AuthEnabled);
}
