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

    /// <summary>
    /// Path to a PKCS#12 (.pfx) certificate used to sign tokens. When unset, an
    /// ephemeral key is used (fine for a single instance; set a real certificate for
    /// multi-instance deployments so tokens survive restarts).
    /// </summary>
    public string? SigningCertificatePath { get; set; }
    public string? SigningCertificatePassword { get; set; }

    /// <summary>Path to a PKCS#12 (.pfx) certificate used for token encryption (optional).</summary>
    public string? EncryptionCertificatePath { get; set; }
    public string? EncryptionCertificatePassword { get; set; }
}

/// <summary>A login identity for the interactive (authorization-code) flow.</summary>
public sealed class AuthUser
{
    public string Username { get; set; } = string.Empty;

    /// <summary>Plaintext password (dev). Prefer <see cref="PasswordHash"/> in production.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash (<c>pbkdf2$iterations$salt$hash</c>); takes precedence over <see cref="Password"/>.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Optional display name; falls back to the username.</summary>
    public string? Name { get; set; }

    /// <summary>Verifies a candidate password (constant-time), preferring the hash when present.</summary>
    public bool VerifyPassword(string candidate) =>
        !string.IsNullOrEmpty(PasswordHash)
            ? PasswordHasher.VerifyHash(candidate, PasswordHash)
            : PasswordHasher.VerifyPlaintext(candidate, Password);
}
