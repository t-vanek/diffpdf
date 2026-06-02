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
        services.AddScoped<IJobSubmissionService, SqlServerJobSubmissionService>();

        services.AddHostedService(sp => new SqlServerMigrationHostedService(
            connectionString, sp.GetRequiredService<ILogger<SqlServerMigrationHostedService>>()));

        return services;
    }
}

/// <summary>Runs the schema migration once on startup, before message processing begins.</summary>
internal sealed class SqlServerMigrationHostedService(string connectionString, ILogger<SqlServerMigrationHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Running SQL Server schema migration");
        await SqlServerMigrator.MigrateAsync(connectionString, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
