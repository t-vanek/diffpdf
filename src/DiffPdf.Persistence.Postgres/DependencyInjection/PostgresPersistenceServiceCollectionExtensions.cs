using DiffPdf.Persistence;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;

namespace DiffPdf.Persistence.Postgres.DependencyInjection;

public static class PostgresPersistenceServiceCollectionExtensions
{
    /// <summary>Registers the EF Core (PostgreSQL) stores, Mapperly mapper, transactional outbox and migration.</summary>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextWithWolverineIntegration<DiffPdfDbContext>(o => o.UseNpgsql(connectionString));

        services.AddSingleton<EntityMapper>();
        services.AddScoped<IJobStore, PostgresJobStore>();
        services.AddScoped<IFilePairTaskStore, PostgresFilePairTaskStore>();
        services.AddScoped<IBranchStore, PostgresBranchStore>();
        services.AddScoped<IInstanceStore, PostgresInstanceStore>();
        services.AddScoped<ISubscriptionStore, PostgresSubscriptionStore>();
        services.AddScoped<IControlCheckStore, PostgresControlCheckStore>();
        services.AddScoped<IControlCheckRunStore, PostgresControlCheckRunStore>();
        services.AddScoped<ITriggerStore, PostgresTriggerStore>();
        services.AddScoped<ITriggerRunStore, PostgresTriggerRunStore>();
        services.AddScoped<IAuditLogStore, PostgresAuditLogStore>();
        services.AddScoped<IApiKeyStore, PostgresApiKeyStore>();
        services.AddScoped<IJobSubmissionService, PostgresJobSubmissionService>();
        services.AddSingleton<ILeaderElection>(new PostgresLeaderElection(connectionString));

        services.AddHostedService<EfCoreMigrationBackgroundService>();

        return services;
    }
}

/// <summary>
/// Applies EF Core migrations once the database is reachable. Runs as a background service so a missing
/// or not-yet-ready database never blocks (or crashes) host startup: it retries the connection with
/// exponential backoff and, once connected, applies only the pending migrations
/// (<see cref="RelationalDatabaseFacadeExtensions.GetPendingMigrationsAsync"/> /
/// <c>__EFMigrationsHistory</c> — a fresh database gets everything, an up-to-date one is a no-op).
/// </summary>
internal sealed class EfCoreMigrationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<EfCoreMigrationBackgroundService> logger)
    : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        bool waitingLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<DiffPdfDbContext>();

                if (!await db.Database.CanConnectAsync(stoppingToken))
                {
                    if (!waitingLogged)
                    {
                        logger.LogWarning("Database not reachable yet; retrying until it is available before migrating.");
                        waitingLogged = true;
                    }
                    await Task.Delay(delay, stoppingToken);
                    delay = NextDelay(delay);
                    continue;
                }

                var pending = (await db.Database.GetPendingMigrationsAsync(stoppingToken)).ToList();
                if (pending.Count == 0)
                {
                    logger.LogInformation("Database schema is up to date; no migrations to apply.");
                    return;
                }

                logger.LogInformation("Applying {Count} pending database migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(stoppingToken);
                logger.LogInformation("Database migration complete.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Database migration attempt failed; retrying in {Delay}s.", delay.TotalSeconds);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { return; }
                delay = NextDelay(delay);
            }
        }
    }

    private static TimeSpan NextDelay(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, current.TotalSeconds * 2));
}
