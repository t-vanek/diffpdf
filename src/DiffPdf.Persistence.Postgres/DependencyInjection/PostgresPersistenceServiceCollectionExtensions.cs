using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DiffPdf.Persistence.Postgres.DependencyInjection;

public static class PostgresPersistenceServiceCollectionExtensions
{
    /// <summary>Registers the PostgreSQL-backed job / business-instance / project stores.</summary>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddSingleton<IJobStore, PostgresJobStore>();
        services.AddSingleton<IBusinessInstanceStore, PostgresBusinessInstanceStore>();
        services.AddSingleton<IProjectStore, PostgresProjectStore>();
        services.AddHostedService<PostgresMigrationHostedService>();
        return services;
    }
}

/// <summary>Runs the schema migration once on startup, before message processing begins.</summary>
internal sealed class PostgresMigrationHostedService(
    NpgsqlDataSource dataSource,
    ILogger<PostgresMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Running PostgreSQL schema migration");
        await PostgresMigrator.MigrateAsync(dataSource, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
