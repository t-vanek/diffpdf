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
    /// Wires OpenIddict as an embedded OAuth2 server (client-credentials flow) plus
    /// token validation, and makes every endpoint require a valid token by default.
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
                o.SetTokenEndpointUris("connect/token");
                o.AllowClientCredentialsFlow();
                o.RegisterScopes(auth.Scope);

                // Ephemeral keys are fine for short-lived M2M tokens validated by this
                // same server; use real certificates for multi-instance production.
                o.AddEphemeralEncryptionKey();
                o.AddEphemeralSigningKey();
                o.DisableAccessTokenEncryption(); // emit a readable JWT bearer

                // The API is expected to run behind TLS termination; allow plain
                // HTTP at the app so the token endpoint works without app-level HTTPS.
                o.UseAspNetCore().EnableTokenEndpointPassthrough().DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        var requireAuth = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(requireAuth)
            .SetFallbackPolicy(requireAuth); // every endpoint requires a token unless AllowAnonymous

        services.AddHostedService<OpenIddictClientSeeder>();
    }

    /// <summary>Token endpoint for the client-credentials grant.</summary>
    public static void MapTokenEndpoint(this WebApplication app, AuthOptions auth)
    {
        app.MapPost("/connect/token", (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            if (!request.IsClientCredentialsGrantType())
                return Results.BadRequest(new { error = "unsupported_grant_type" });

            // Client id/secret are already validated by OpenIddict against the store.
            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                Claims.Name, Claims.Role);

            identity.AddClaim(Claims.Subject, request.ClientId!);
            identity.AddClaim(Claims.Name, request.ClientId!);

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes().Any() ? request.GetScopes() : [auth.Scope]);
            identity.SetDestinations(_ => [Destinations.AccessToken]);

            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }).AllowAnonymous();
    }
}
