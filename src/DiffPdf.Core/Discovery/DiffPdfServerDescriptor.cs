namespace DiffPdf.Core.Discovery;

/// <summary>
/// Self-description a diffpdf server advertises on the network (over UDP) and
/// exposes at <c>GET /api/v1/server-info</c>. A client uses it to connect and,
/// when auth is on, to learn how to obtain a token.
/// </summary>
public sealed record DiffPdfServerDescriptor
{
    /// <summary>Service identifier, always <c>diffpdf</c>.</summary>
    public string Service { get; init; } = "diffpdf";

    /// <summary>Stable id of this server process (survives for the process lifetime).</summary>
    public required string InstanceId { get; init; }

    /// <summary>Human-friendly server name (defaults to the machine name).</summary>
    public required string Name { get; init; }

    /// <summary>Server version (informational/assembly version).</summary>
    public required string Version { get; init; }

    /// <summary>Base URL a client should call, e.g. <c>http://192.168.1.10:8080</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Base path of the versioned REST API.</summary>
    public string ApiBasePath { get; init; } = "/api/v1";

    /// <summary>Path of the SignalR job-progress hub.</summary>
    public string HubPath { get; init; } = "/hubs/jobs";

    /// <summary>Whether the API requires a bearer token.</summary>
    public bool AuthEnabled { get; init; }

    /// <summary>OAuth2 token endpoint (when <see cref="AuthEnabled"/>).</summary>
    public string? TokenEndpoint { get; init; }

    /// <summary>Grant types the server supports (e.g. client_credentials, authorization_code, refresh_token).</summary>
    public IReadOnlyList<string> GrantTypes { get; init; } = [];

    /// <summary>When this descriptor was produced (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Absolute URL of the REST API root (<see cref="BaseUrl"/> + <see cref="ApiBasePath"/>).</summary>
    public string ApiBaseUrl => $"{BaseUrl.TrimEnd('/')}{ApiBasePath}";

    /// <summary>Absolute URL of the progress hub.</summary>
    public string HubUrl => $"{BaseUrl.TrimEnd('/')}{HubPath}";
}
