namespace DiffPdf.Core.Discovery;

/// <summary>
/// Configures the UDP network-discovery responder, which lets clients (e.g. a WPF
/// app) find the server on the LAN without a hard-coded address.
/// </summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    /// <summary>Turn the UDP responder on. The <c>/api/v1/server-info</c> endpoint is always available.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UDP port the responder listens on (and clients probe).</summary>
    public int Port { get; set; } = DiscoveryProtocol.DefaultPort;

    /// <summary>Multicast group joined in addition to receiving broadcasts.</summary>
    public string MulticastAddress { get; set; } = DiscoveryProtocol.DefaultMulticastAddress;

    /// <summary>Advertised server name; defaults to the machine name.</summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Explicit base URL to advertise (e.g. behind a reverse proxy / fixed DNS name).
    /// When unset, the responder derives it from the local interface + the HTTP port.
    /// </summary>
    public string? AdvertisedBaseUrl { get; set; }
}
