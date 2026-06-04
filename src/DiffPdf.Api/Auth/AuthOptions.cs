namespace DiffPdf.Api.Auth;

/// <summary>OAuth2 / OpenID Connect configuration.</summary>
public sealed class AuthOptions
{
    /// <summary>Turn authentication on. Requires a configured PostgreSQL / SQL Server connection.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seeded client for the client-credentials (machine-to-machine) flow.</summary>
    public string ClientId { get; set; } = "diffpdf-ci";
    public string ClientSecret { get; set; } = "diffpdf-secret";
    public string Scope { get; set; } = "diffpdf.api";

    /// <summary>Role granted to the seeded M2M client's tokens (Viewer / Operator / Admin). Default Admin
    /// so the desktop/CI keep full access; lower it to restrict the built-in client.</summary>
    public string Role { get; set; } = "Admin";

    /// <summary>Access-token lifetime (minutes).</summary>
    public int AccessTokenMinutes { get; set; } = 60;
}
