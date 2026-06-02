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
        if (await manager.FindByClientIdAsync(auth.ClientId, cancellationToken) is not null)
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
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + auth.Scope,
            },
        }, cancellationToken);

        logger.LogInformation("Seeded OpenIddict client '{ClientId}'", auth.ClientId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
