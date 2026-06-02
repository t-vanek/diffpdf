using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DiffPdf.Api.Auth;

/// <summary>Creates the OpenIddict tables and seeds the client-credentials application on startup.</summary>
internal sealed class OpenIddictClientSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthOptions> options,
    ILogger<OpenIddictClientSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var auth = options.Value;
        await using var scope = scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        await SeedMachineClientAsync(manager, auth, cancellationToken);
        await SeedInteractiveClientAsync(manager, auth, cancellationToken);
    }

    /// <summary>Confidential client for the client-credentials (M2M / CI) flow.</summary>
    private async Task SeedMachineClientAsync(IOpenIddictApplicationManager manager, AuthOptions auth, CancellationToken ct)
    {
        if (await manager.FindByClientIdAsync(auth.ClientId, ct) is not null)
            return;

        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = auth.ClientId,
            ClientSecret = auth.ClientSecret,
            ClientType = ClientTypes.Confidential,
            DisplayName = "diffpdf machine-to-machine client",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + auth.Scope,
            },
        }, ct);

        logger.LogInformation("Seeded OpenIddict M2M client '{ClientId}'", auth.ClientId);
    }

    /// <summary>Public client for the interactive authorization-code + PKCE + refresh flow.</summary>
    private async Task SeedInteractiveClientAsync(IOpenIddictApplicationManager manager, AuthOptions auth, CancellationToken ct)
    {
        if (await manager.FindByClientIdAsync(auth.InteractiveClientId, ct) is not null)
            return;

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = auth.InteractiveClientId,
            ClientType = ClientTypes.Public, // no secret; PKCE-protected
            DisplayName = "diffpdf interactive client",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + auth.Scope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var uri in auth.RedirectUris)
            descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in auth.PostLogoutRedirectUris)
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));

        await manager.CreateAsync(descriptor, ct);

        logger.LogInformation("Seeded OpenIddict interactive client '{ClientId}'", auth.InteractiveClientId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
