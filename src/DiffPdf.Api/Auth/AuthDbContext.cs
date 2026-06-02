using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Api.Auth;

/// <summary>
/// Dedicated EF Core context holding only OpenIddict's tables, kept separate
/// from the application schema so it can be created independently.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.UseOpenIddict();
    }
}

/// <summary>OAuth2 / OpenID Connect configuration.</summary>
public sealed class AuthOptions
{
    /// <summary>Turn authentication on. Requires a configured PostgreSQL / SQL Server connection.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seeded client for the client-credentials (machine-to-machine) flow.</summary>
    public string ClientId { get; set; } = "diffpdf-ci";
    public string ClientSecret { get; set; } = "diffpdf-secret";
    public string Scope { get; set; } = "diffpdf.api";

    /// <summary>
    /// Seeded public client for the interactive authorization-code + PKCE flow.
    /// Public clients hold no secret and rely on PKCE.
    /// </summary>
    public string InteractiveClientId { get; set; } = "diffpdf-app";

    /// <summary>Allowed redirect URIs for the interactive client (defaults to the Swagger UI redirect).</summary>
    public IList<string> RedirectUris { get; set; } = new List<string>
    {
        "http://localhost:8080/swagger/oauth2-redirect.html",
    };

    /// <summary>Allowed post-logout redirect URIs for the interactive client.</summary>
    public IList<string> PostLogoutRedirectUris { get; set; } = new List<string>();

    /// <summary>
    /// Users accepted by the interactive login. Config-backed so the flow works out of
    /// the box; swap for a real identity store in production. Passwords are compared as-is.
    /// </summary>
    public IList<AuthUser> Users { get; set; } = new List<AuthUser>();

    /// <summary>Access-token lifetime (minutes).</summary>
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>Refresh-token lifetime (days). Refresh tokens rotate on each use.</summary>
    public int RefreshTokenDays { get; set; } = 14;
}

/// <summary>A login identity for the interactive (authorization-code) flow.</summary>
public sealed class AuthUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Optional display name; falls back to the username.</summary>
    public string? Name { get; set; }
}
