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
        services.AddScoped<IScheduleStore, PostgresScheduleStore>();
        services.AddScoped<ISubscriptionStore, PostgresSubscriptionStore>();
        services.AddScoped<IJobSubmissionService, PostgresJobSubmissionService>();

        services.AddHostedService(sp => new PostgresMigrationHostedService(
            connectionString, sp.GetRequiredService<ILogger<PostgresMigrationHostedService>>()));

        return services;
    }
}

/// <summary>Runs the schema migration once on startup, before message processing begins.</summary>
internal sealed class PostgresMigrationHostedService(string connectionString, ILogger<PostgresMigrationHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Running PostgreSQL schema migration");
        await PostgresMigrator.MigrateAsync(connectionString, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
