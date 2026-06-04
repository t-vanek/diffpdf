using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DiffPdf.Api.Auth;

/// <summary>Creates the OpenIddict tables and seeds the machine-to-machine client on startup.</summary>
internal sealed class OpenIddictClientSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthOptions> options,
    ILogger<OpenIddictClientSeeder> logger,
    bool useSqlServer) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var auth = options.Value;
        await using var scope = scopeFactory.CreateAsyncScope();

        // Ensure the schema (incl. the OpenIddict tables) exists before seeding. MigrateAsync is idempotent
        // and serialized by EF's migration lock, so it's safe alongside the persistence migration runner and
        // removes the hosted-service start-order race.
        DbContext db = useSqlServer
            ? scope.ServiceProvider.GetRequiredService<DiffPdf.Persistence.SqlServer.DiffPdfDbContext>()
            : scope.ServiceProvider.GetRequiredService<DiffPdf.Persistence.Postgres.DiffPdfDbContext>();
        await db.Database.MigrateAsync(cancellationToken);

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await SeedMachineClientAsync(manager, auth, cancellationToken);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
