using DiffPdf.Persistence;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer.DependencyInjection;

public static class SqlServerPersistenceServiceCollectionExtensions
{
    /// <summary>Registers the EF Core (SQL Server) stores, Mapperly mapper, transactional outbox and migration.</summary>
    public static IServiceCollection AddSqlServerPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextWithWolverineIntegration<DiffPdfDbContext>(o => o.UseSqlServer(connectionString));

        services.AddSingleton<EntityMapper>();
        services.AddScoped<IJobStore, SqlServerJobStore>();
        services.AddScoped<IFilePairTaskStore, SqlServerFilePairTaskStore>();
        services.AddScoped<IBranchStore, SqlServerBranchStore>();
        services.AddScoped<IInstanceStore, SqlServerInstanceStore>();
        services.AddScoped<ISubscriptionStore, SqlServerSubscriptionStore>();
        services.AddScoped<INotificationDeliveryStore, SqlServerNotificationDeliveryStore>();
        services.AddScoped<ISystemEventStore, SqlServerSystemEventStore>();
        services.AddScoped<IEmailSettingsStore, SqlServerEmailSettingsStore>();
        services.AddScoped<IAutomationStore, SqlServerAutomationStore>();
        services.AddScoped<IAutomationRunStore, SqlServerAutomationRunStore>();
        services.AddScoped<ITriggerStore, SqlServerTriggerStore>();
        services.AddScoped<ITriggerRunStore, SqlServerTriggerRunStore>();
        services.AddScoped<IAuditLogStore, SqlServerAuditLogStore>();
        services.AddScoped<IScopeConfigurationStore, SqlServerScopeConfigurationStore>();
        services.AddScoped<IJobSubmissionService, SqlServerJobSubmissionService>();
        services.AddSingleton<ILeaderElection>(new SqlServerLeaderElection(connectionString));

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
                }
                else
                {
                    logger.LogInformation("Applying {Count} pending database migration(s): {Migrations}",
                        pending.Count, string.Join(", ", pending));
                    await db.Database.MigrateAsync(stoppingToken);
                    logger.LogInformation("Database migration complete.");
                }

                // One-time (idempotent) backfill of denormalized verdict counts for jobs completed before the
                // AddJobVerdictColumns migration — so the jobs list keeps showing their verdict without the report.
                await BackfillJobVerdictsAsync(scope.ServiceProvider, stoppingToken);
                // One-time (idempotent) backfill of the denormalized task verdict (ResultStatus) so the client's
                // "Jen odlišné" filter is correct for pairs completed before the AddTaskResultStatus migration.
                await BackfillTaskResultStatusAsync(scope.ServiceProvider, stoppingToken);
                // One-time heal: skip Queued pairs left stranded by a cancel that predated the cancel→skip fix.
                await SkipStrandedTasksAsync(scope.ServiceProvider, stoppingToken);
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

    private async Task BackfillJobVerdictsAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var jobs = services.GetRequiredService<IJobStore>();
            int total = 0, batch;
            do { batch = await jobs.BackfillVerdictsAsync(500, ct); total += batch; }
            while (batch > 0 && !ct.IsCancellationRequested);
            if (total > 0) logger.LogInformation("Backfilled verdict counts for {Count} completed job(s).", total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Job verdict backfill failed (non-fatal; the list falls back to a null verdict).");
        }
    }

    private async Task BackfillTaskResultStatusAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var tasks = services.GetRequiredService<IFilePairTaskStore>();
            int total = 0, batch;
            do { batch = await tasks.BackfillResultStatusAsync(500, ct); total += batch; }
            while (batch > 0 && !ct.IsCancellationRequested);
            if (total > 0) logger.LogInformation("Backfilled verdict (ResultStatus) for {Count} completed file-pair task(s).", total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task result-status backfill failed (non-fatal; 'only differing' may miss old pairs).");
        }
    }

    private async Task SkipStrandedTasksAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            int n = await services.GetRequiredService<IFilePairTaskStore>().SkipPendingForTerminalJobsAsync(ct);
            if (n > 0) logger.LogInformation("Skipped {Count} stranded Queued pair(s) belonging to terminal jobs.", n);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stranded-task cleanup failed (non-fatal).");
        }
    }

    private static TimeSpan NextDelay(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, current.TotalSeconds * 2));
}
