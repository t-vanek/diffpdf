using System.Security.Claims;
using System.Text.Encodings.Web;
using DiffPdf.Core.Security;
using DiffPdf.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Auth;

/// <summary>Constants for the third-party API-key authentication scheme.</summary>
public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates a request by its <c>X-Api-Key</c> header: SHA-256 hash → <see cref="IApiKeyStore"/> lookup,
/// validates the key is usable (enabled, not revoked/deleted/expired), and builds a role-bearing principal.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore store)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values))
            return AuthenticateResult.NoResult();

        var presented = values.ToString();
        if (string.IsNullOrWhiteSpace(presented))
            return AuthenticateResult.NoResult();

        var key = await store.FindByHashAsync(ApiKeyHasher.Hash(presented));
        if (key is null || !key.IsUsable(DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("Invalid or inactive API key.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new("name", key.Name),
            new("auth_method", "apikey"),
        };
        claims.AddRange(RoleClaims.ClaimsFor(key.Role));

        var identity = new ClaimsIdentity(claims, ApiKeyDefaults.Scheme, nameType: "name", roleType: RoleClaims.ClaimType);
        var principal = new ClaimsPrincipal(identity);

        // Best-effort last-used stamp; never block (or fail) the request on it.
        _ = store.TouchLastUsedAsync(key.Id, DateTimeOffset.UtcNow);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyDefaults.Scheme));
    }
}
