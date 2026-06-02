using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DiffPdf.Core.Discovery;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Discovery;

/// <summary>
/// Answers UDP discovery probes so LAN clients can find this server without a
/// hard-coded address. Listens for broadcasts and on a multicast group; replies
/// (unicast) to each probe with the server descriptor.
/// </summary>
public sealed class UdpDiscoveryResponder(
    IServerDescriptorProvider descriptors,
    IOptions<DiscoveryOptions> options,
    ILogger<UdpDiscoveryResponder> logger) : BackgroundService
{
    private readonly DiscoveryOptions _options = options.Value;
    private readonly IReadOnlyList<IpSubnet> _allowedSubnets = ParseSubnets(options.Value.AllowedClientSubnets);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient socket;
        try
        {
            socket = CreateSocket();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UDP discovery responder disabled: could not bind port {Port}.", _options.Port);
            return;
        }

        logger.LogInformation(
            "UDP discovery responder listening on port {Port} (multicast {Group}).",
            _options.Port, _options.MulticastAddress);

        using (socket)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "UDP discovery receive error; continuing.");
                    continue;
                }

                if (!DiscoveryProtocol.IsProbe(received.Buffer))
                    continue;

                if (!IsAllowedClient(received.RemoteEndPoint.Address))
                {
                    logger.LogDebug("Ignoring discovery probe from disallowed source {Remote}.", received.RemoteEndPoint);
                    continue;
                }

                try
                {
                    string baseUrl = ResolveBaseUrl(received.RemoteEndPoint);
                    byte[] response = DiscoveryProtocol.CreateResponse(descriptors.Create(baseUrl));
                    await socket.SendAsync(response, response.Length, received.RemoteEndPoint);
                    logger.LogDebug("Answered discovery probe from {Remote} with {BaseUrl}.", received.RemoteEndPoint, baseUrl);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to answer discovery probe from {Remote}.", received.RemoteEndPoint);
                }
            }
        }
    }

    private UdpClient CreateSocket()
    {
        var socket = new UdpClient { EnableBroadcast = true };
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, _options.Port));

        if (IPAddress.TryParse(_options.MulticastAddress, out var group) && group.AddressFamily == AddressFamily.InterNetwork)
            JoinMulticastOnAllInterfaces(socket, group);

        return socket;
    }

    /// <summary>Joins the multicast group on every up, multicast-capable IPv4 interface (multi-homed hosts).</summary>
    private void JoinMulticastOnAllInterfaces(UdpClient socket, IPAddress group)
    {
        bool joinedAny = false;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                try
                {
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, unicast.Address));
                    joinedAny = true;
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "Could not join {Group} on {Interface}.", group, unicast.Address);
                }
            }
        }

        if (!joinedAny)
        {
            try { socket.JoinMulticastGroup(group); }
            catch (SocketException ex) { logger.LogDebug(ex, "Could not join multicast group {Group}.", group); }
        }
    }

    private bool IsAllowedClient(IPAddress source)
    {
        if (_allowedSubnets.Count == 0)
            return true; // no restriction configured

        foreach (var subnet in _allowedSubnets)
        {
            if (subnet.Contains(source))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<IpSubnet> ParseSubnets(IEnumerable<string> cidrs)
    {
        var result = new List<IpSubnet>();
        foreach (var cidr in cidrs)
        {
            if (IpSubnet.TryParse(cidr, out var subnet) && subnet is not null)
                result.Add(subnet);
        }
        return result;
    }

    /// <summary>Picks the base URL to advertise: the configured override, or the local interface IP + HTTP port.</summary>
    private string ResolveBaseUrl(IPEndPoint remote)
    {
        if (!string.IsNullOrWhiteSpace(_options.AdvertisedBaseUrl))
            return _options.AdvertisedBaseUrl;

        int port = descriptors.GetHttpPort() ?? 8080;
        IPAddress local = LocalAddressToReach(remote.Address);
        return $"http://{local}:{port}";
    }

    /// <summary>Finds the local interface address the OS would use to reach <paramref name="remote"/>.</summary>
    private static IPAddress LocalAddressToReach(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 9); // discard port; no packets are sent for UDP "connect"
            if (probe.LocalEndPoint is IPEndPoint local && !local.Address.Equals(IPAddress.Any))
                return local.Address;
        }
        catch (SocketException)
        {
            // Fall through to loopback.
        }

        return remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    }
}
