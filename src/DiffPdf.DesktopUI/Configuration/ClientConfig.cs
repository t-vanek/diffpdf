namespace DiffPdf.DesktopUI.Configuration;

/// <summary>Desktop client configuration, loaded from appsettings.json (+ <c>DIFFPDF_</c> env vars).</summary>
public sealed class ClientConfig
{
    public ServerConfig Server { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
}

/// <summary>How the client reaches the server.</summary>
public sealed class ServerConfig
{
    /// <summary>Explicit server URL/IP. When set, the client connects here and discovery is skipped.</summary>
    public string? Url { get; set; }

    /// <summary>Connect automatically on startup (explicit <see cref="Url"/> if set, otherwise discovery).</summary>
    public bool AutoConnect { get; set; } = true;

    public DiscoveryConfig Discovery { get; set; } = new();
}

/// <summary>LAN auto-discovery settings (must line up with the server's <c>Discovery</c> section).</summary>
public sealed class DiscoveryConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>UDP port to broadcast probes to.</summary>
    public int Port { get; set; } = 5276;

    /// <summary>How long to wait for a discovery reply.</summary>
    public int TimeoutSeconds { get; set; } = 2;

    /// <summary>Port tried on <c>localhost</c> as a fast path before broadcasting.</summary>
    public int DefaultPort { get; set; } = 5275;
}

/// <summary>Optional M2M credentials, used only when the server requires auth.</summary>
public sealed class AuthConfig
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
