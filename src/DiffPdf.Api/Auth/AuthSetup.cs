using System.Security.Claims;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
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
    /// client-credentials (M2M) flow, plus token validation. Every endpoint
    /// requires a valid token by default.
    /// </summary>
    public static void AddDiffPdfAuth(this IServiceCollection services, bool useSqlServer, AuthOptions auth)
    {
        // OpenIddict shares the application's DbContext: its tables are created and versioned by the EF
        // migration runner (the persistence layer already registered the context). No separate AuthDbContext.
        services.AddOpenIddict()
            .AddCore(o =>
            {
                var ef = o.UseEntityFrameworkCore();
                if (useSqlServer)
                    ef.UseDbContext<DiffPdf.Persistence.SqlServer.DiffPdfDbContext>();
                else
                    ef.UseDbContext<DiffPdf.Persistence.Postgres.DiffPdfDbContext>();
            })
            .AddServer(o =>
            {
                // Standard endpoints — OpenIddict generates and protocol-handles them;
                // the token passthrough below is completed by our minimal handler.
                o.SetTokenEndpointUris("connect/token");
                o.SetRevocationEndpointUris("connect/revocation"); // fully handled by OpenIddict

                // Machine-to-machine client authentication.
                o.AllowClientCredentialsFlow();

                o.RegisterScopes(auth.Scope);

                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(auth.AccessTokenMinutes));

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

        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<OpenIddictClientSeeder>(sp, useSqlServer));
    }

    /// <summary>
    /// Token endpoint for the client-credentials (M2M) grant. The client id/secret
    /// are validated by OpenIddict; this handler mints the access token.
    /// </summary>
    public static void MapTokenEndpoint(this WebApplication app, AuthOptions auth)
    {
        app.MapPost("/connect/token", (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            if (!request.IsClientCredentialsGrantType())
                return Results.BadRequest(new { error = Errors.UnsupportedGrantType });

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
        }).AllowAnonymous();
    }

    /// <summary>Decides which tokens each claim is copied into.</summary>
    internal static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        Claims.Name or Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken],
    };
}
