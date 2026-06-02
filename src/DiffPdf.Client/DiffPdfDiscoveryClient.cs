using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DiffPdf.Core.Discovery;

namespace DiffPdf.Client;

/// <summary>Finds diffpdf servers on the local network via UDP broadcast + multicast.</summary>
public interface IDiffPdfDiscoveryClient
{
    /// <summary>Probes the LAN and collects every server that answers within <paramref name="timeout"/>.</summary>
    Task<IReadOnlyList<DiffPdfServerDescriptor>> DiscoverAsync(
        TimeSpan timeout, int port = DiscoveryProtocol.DefaultPort, CancellationToken ct = default);

    /// <summary>Returns the first server that answers, or null if none did before the timeout.</summary>
    Task<DiffPdfServerDescriptor?> DiscoverFirstAsync(
        TimeSpan timeout, int port = DiscoveryProtocol.DefaultPort, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DiffPdfDiscoveryClient : IDiffPdfDiscoveryClient
{
    public async Task<IReadOnlyList<DiffPdfServerDescriptor>> DiscoverAsync(
        TimeSpan timeout, int port = DiscoveryProtocol.DefaultPort, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiffPdfServerDescriptor>(StringComparer.Ordinal);

        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        byte[] probe = DiscoveryProtocol.CreateProbe();
        await SendProbeAsync(client, probe, port, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(deadline.Token);
                var descriptor = DiscoveryProtocol.TryParseResponse(result.Buffer);
                if (descriptor is not null)
                    found[descriptor.InstanceId] = descriptor;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout reached — return whatever answered.
        }

        return found.Values.ToList();
    }

    public async Task<DiffPdfServerDescriptor?> DiscoverFirstAsync(
        TimeSpan timeout, int port = DiscoveryProtocol.DefaultPort, CancellationToken ct = default)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        byte[] probe = DiscoveryProtocol.CreateProbe();
        await SendProbeAsync(client, probe, port, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(deadline.Token);
                var descriptor = DiscoveryProtocol.TryParseResponse(result.Buffer);
                if (descriptor is not null)
                    return descriptor;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — none found.
        }

        return null;
    }

    private static async Task SendProbeAsync(UdpClient client, byte[] probe, int port, CancellationToken ct)
    {
        // Global broadcast (default interface) and multicast.
        await client.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, port));

        if (IPAddress.TryParse(DiscoveryProtocol.DefaultMulticastAddress, out var group))
            await client.SendAsync(probe, probe.Length, new IPEndPoint(group, port));

        // Directed broadcast per interface so multi-homed clients (Wi-Fi + LAN, VPN) reach every subnet.
        foreach (var broadcast in DirectedBroadcastAddresses())
        {
            try { await client.SendAsync(probe, probe.Length, new IPEndPoint(broadcast, port)); }
            catch (SocketException) { /* interface may be down between enumeration and send */ }
        }

        ct.ThrowIfCancellationRequested();
    }

    private static IEnumerable<IPAddress> DirectedBroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;

                byte[] address = unicast.Address.GetAddressBytes();
                byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.Length != address.Length)
                    continue;

                var broadcast = new byte[address.Length];
                for (int i = 0; i < address.Length; i++)
                    broadcast[i] = (byte)(address[i] | ~mask[i]);

                yield return new IPAddress(broadcast);
            }
        }
    }
}
