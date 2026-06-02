using System.Security.Claims;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DiffPdf.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Wires OpenIddict as an embedded OAuth2 server exposing the standard
    /// auto-generated endpoints — token and revocation — supporting the
    /// client-credentials (M2M) and refresh-token flows, plus token validation.
    /// Every endpoint requires a valid token by default.
    /// </summary>
    public static void AddDiffPdfAuth(this IServiceCollection services, string connectionString, bool useSqlServer, AuthOptions auth)
    {
        services.AddDbContext<AuthDbContext>(o =>
        {
            if (useSqlServer)
                o.UseSqlServer(connectionString);
            else
                o.UseNpgsql(connectionString);
            o.UseOpenIddict();
        });

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
            .AddServer(o =>
            {
                // Standard endpoints — OpenIddict generates and protocol-handles them;
                // the token passthrough below is completed by our minimal handler.
                o.SetTokenEndpointUris("connect/token");
                o.SetRevocationEndpointUris("connect/revocation"); // fully handled by OpenIddict

                // Machine-to-machine client authentication plus refresh tokens.
                o.AllowClientCredentialsFlow();
                o.AllowRefreshTokenFlow();

                o.RegisterScopes(auth.Scope, Scopes.OfflineAccess);

                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(auth.AccessTokenMinutes));
                o.SetRefreshTokenLifetime(TimeSpan.FromDays(auth.RefreshTokenDays));

                // Ephemeral keys are fine for short-lived tokens validated by this same
                // server; use real certificates for multi-instance production.
                o.AddEphemeralEncryptionKey();
                o.AddEphemeralSigningKey();
                o.DisableAccessTokenEncryption(); // emit a readable JWT bearer

                // The API is expected to run behind TLS termination; allow plain HTTP at
                // the app so the endpoints work without app-level HTTPS.
                o.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        // Bearer validation protects the API.
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        var requireAuth = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(requireAuth)
            .SetFallbackPolicy(requireAuth); // every endpoint requires a token unless AllowAnonymous

        services.AddHostedService<OpenIddictClientSeeder>();
    }

    /// <summary>
    /// Token endpoint for the enabled grants: client-credentials (M2M) plus the
    /// refresh-token grant. For the latter the subject identity is recovered from
    /// the incoming refresh token.
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

            if (request.IsRefreshTokenGrantType())
            {
                // Recover the principal persisted with the refresh token.
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
