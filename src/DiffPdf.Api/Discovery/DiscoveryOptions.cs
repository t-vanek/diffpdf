namespace DiffPdf.Api.Discovery;

/// <summary>Configures the LAN discovery responder (<see cref="ServerDiscoveryResponder"/>).</summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    /// <summary>When false, the server does not answer discovery probes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UDP port the client broadcasts probes to (must match the client's configured port).</summary>
    public int Port { get; set; } = 5276;
}
