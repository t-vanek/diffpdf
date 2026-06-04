using System.Security.Claims;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions; // OpenIddictExtensions: IsClientCredentialsGrantType / Get/SetScopes / SetDestinations / AddClaim
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DiffPdf.Api.Auth;

public static class AuthSetup
{
    /// <summary>Composite authentication scheme: routes to API-key when <c>X-Api-Key</c> is present, else bearer.</summary>
    public const string MultiScheme = "Smart";

    /// <summary>
    /// Wires OpenIddict as an embedded OAuth2 server (client-credentials / M2M flow) plus token validation,
    /// and a third-party API-key scheme. A composite scheme picks the right one per request. Authorization
    /// policies are registered separately via <see cref="AddDiffPdfAuthorization"/>.
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

        // Composite scheme: bearer (OpenIddict) by default, API-key when the X-Api-Key header is present.
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = MultiScheme;
                o.DefaultChallengeScheme = MultiScheme;
            })
            .AddPolicyScheme(MultiScheme, "Bearer or API key", o =>
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.ContainsKey(ApiKeyDefaults.HeaderName)
                        ? ApiKeyDefaults.Scheme
                        : OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });

        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<OpenIddictClientSeeder>(sp, useSqlServer));
    }

    /// <summary>
    /// Registers the read / write / admin authorization policies and the fallback. When auth is enabled the
    /// policies enforce role claims (read=Viewer, write=Operator, admin=Admin); when disabled they are
    /// permissive so every endpoint (including those marked <c>RequireWrite/RequireAdmin</c>) stays open in dev.
    /// </summary>
    public static void AddDiffPdfAuthorization(this IServiceCollection services, bool authEnabled)
    {
        var builder = services.AddAuthorizationBuilder();

        if (authEnabled)
        {
            var read = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().RequireClaim(RoleClaims.ClaimType, nameof(DiffPdf.Core.Models.Role.Viewer)).Build();

            builder
                .AddPolicy(RolePolicies.Read, p => p.RequireAuthenticatedUser().RequireClaim(RoleClaims.ClaimType, nameof(DiffPdf.Core.Models.Role.Viewer)))
                .AddPolicy(RolePolicies.Write, p => p.RequireAuthenticatedUser().RequireClaim(RoleClaims.ClaimType, nameof(DiffPdf.Core.Models.Role.Operator)))
                .AddPolicy(RolePolicies.Admin, p => p.RequireAuthenticatedUser().RequireClaim(RoleClaims.ClaimType, nameof(DiffPdf.Core.Models.Role.Admin)))
                .SetDefaultPolicy(read)
                .SetFallbackPolicy(read);
        }
        else
        {
            var allow = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
            builder
                .AddPolicy(RolePolicies.Read, allow)
                .AddPolicy(RolePolicies.Write, allow)
                .AddPolicy(RolePolicies.Admin, allow)
                .SetDefaultPolicy(allow)
                .SetFallbackPolicy(allow);
        }
    }

    /// <summary>
    /// Token endpoint for the client-credentials (M2M) grant. The client id/secret are validated by
    /// OpenIddict; this handler mints the access token and stamps the configured role (hierarchical).
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

            // The seeded M2M client's role (default Admin) — hierarchical role claims drive the policies.
            foreach (var role in RoleClaims.Expand(RoleClaims.Parse(auth.Role)))
                identity.AddClaim(new Claim(RoleClaims.ClaimType, role));

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
        RoleClaims.ClaimType => [Destinations.AccessToken],
        _ => [Destinations.AccessToken],
    };
}
