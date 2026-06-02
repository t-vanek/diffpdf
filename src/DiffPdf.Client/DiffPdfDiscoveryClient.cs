using System.Net;
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
        // Broadcast (same subnet) and multicast (across cooperating switches/interfaces).
        await client.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, port));

        if (IPAddress.TryParse(DiscoveryProtocol.DefaultMulticastAddress, out var group))
            await client.SendAsync(probe, probe.Length, new IPEndPoint(group, port));

        ct.ThrowIfCancellationRequested();
    }
}
