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

    /// <summary>Access-token lifetime (minutes).</summary>
    public int AccessTokenMinutes { get; set; } = 60;
}
