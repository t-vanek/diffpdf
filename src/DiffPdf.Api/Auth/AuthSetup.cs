using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DiffPdf.Api.Auth;

public static class AuthSetup
{
    /// <summary>Cookie scheme that carries the interactive (authorization-code) login session.</summary>
    public const string LoginCookieScheme = "DiffPdf.Login";

    /// <summary>
    /// Wires OpenIddict as an embedded OAuth2 / OIDC server exposing the standard
    /// auto-generated endpoints — token, authorization, userinfo, revocation and
    /// end-session — supporting client-credentials (M2M) and authorization-code +
    /// PKCE + refresh-token (interactive) flows, plus token validation. Every
    /// endpoint requires a valid token by default.
    /// </summary>
    public static void AddDiffPdfAuth(
        this IServiceCollection services, Action<DbContextOptionsBuilder> configureDatabase, AuthOptions auth)
    {
        services.AddDbContext<AuthDbContext>(o =>
        {
            configureDatabase(o);
            o.UseOpenIddict();
        });

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
            .AddServer(o =>
            {
                // Standard endpoints — OpenIddict generates and protocol-handles them;
                // the passthrough ones below are completed by our minimal handlers.
                o.SetTokenEndpointUris("connect/token");
                o.SetAuthorizationEndpointUris("connect/authorize");
                o.SetUserInfoEndpointUris("connect/userinfo");
                o.SetEndSessionEndpointUris("connect/logout");
                o.SetRevocationEndpointUris("connect/revocation"); // fully handled by OpenIddict

                // M2M plus interactive (authorization code + PKCE) and refresh tokens.
                o.AllowClientCredentialsFlow();
                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                o.AllowRefreshTokenFlow();

                o.RegisterScopes(auth.Scope, Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess);

                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(auth.AccessTokenMinutes));
                o.SetRefreshTokenLifetime(TimeSpan.FromDays(auth.RefreshTokenDays));

                // Persistent certificates survive restarts and work across instances;
                // fall back to ephemeral keys when none are configured.
                if (LoadCertificate(auth.SigningCertificatePath, auth.SigningCertificatePassword) is { } signing)
                    o.AddSigningCertificate(signing);
                else
                    o.AddEphemeralSigningKey();

                if (LoadCertificate(auth.EncryptionCertificatePath, auth.EncryptionCertificatePassword) is { } encryption)
                    o.AddEncryptionCertificate(encryption);
                else
                    o.AddEphemeralEncryptionKey();

                o.DisableAccessTokenEncryption(); // emit a readable JWT bearer

                // The API is expected to run behind TLS termination; allow plain HTTP at
                // the app so the endpoints work without app-level HTTPS.
                o.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        // Bearer validation protects the API; a cookie carries the interactive login
        // session consumed by the authorization endpoint.
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddCookie(LoginCookieScheme, o =>
            {
                o.Cookie.Name = LoginCookieScheme;
                o.LoginPath = "/account/login";
                o.ExpireTimeSpan = TimeSpan.FromHours(1);
                o.SlidingExpiration = false;
            });

        var requireAuth = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(requireAuth)
            .SetFallbackPolicy(requireAuth); // every endpoint requires a token unless AllowAnonymous

        // CSRF protection + throttling for the interactive login form.
        services.AddAntiforgery();
        services.AddRateLimiter(o => o.AddPolicy(LoginRateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })));

        services.AddHostedService<OpenIddictClientSeeder>();
    }

    /// <summary>Rate-limit policy name applied to the interactive login endpoint.</summary>
    public const string LoginRateLimitPolicy = "diffpdf-login";

    private static X509Certificate2? LoadCertificate(string? path, string? password) =>
        string.IsNullOrWhiteSpace(path) ? null : X509CertificateLoader.LoadPkcs12FromFile(path, password);

    /// <summary>
    /// Token endpoint for all enabled grants: client-credentials (M2M) plus the
    /// authorization-code and refresh-token grants (interactive). For the latter two
    /// the subject identity is recovered from the incoming code / refresh token.
    /// </summary>
    public static void MapTokenEndpoint(this WebApplication app, AuthOptions auth)
    {
        app.MapPost("/connect/token", async (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            if (request.IsClientCredentialsGrantType())
            {
                // Client id/secret are already validated by OpenIddict against the store.
                var identity = new ClaimsIdentity(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    Claims.Name, Claims.Role);
                identity.AddClaim(Claims.Subject, request.ClientId!);
                identity.AddClaim(Claims.Name, request.ClientId!);

                var principal = new ClaimsPrincipal(identity);
                principal.SetScopes(request.GetScopes().Any() ? request.GetScopes() : [auth.Scope]);
                principal.SetDestinations(GetDestinations);

                return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                // Recover the principal persisted with the authorization code / refresh token.
                var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                if (result.Principal is null)
                    return Results.Forbid(
                        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
                        properties: new AuthenticationProperties(new Dictionary<string, string?>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid.",
                        }));

                result.Principal.SetDestinations(GetDestinations);
                return Results.SignIn(result.Principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return Results.BadRequest(new { error = Errors.UnsupportedGrantType });
        }).AllowAnonymous();
    }

    /// <summary>Decides which tokens each claim is copied into.</summary>
    internal static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        Claims.Name or Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken],
    };
}
