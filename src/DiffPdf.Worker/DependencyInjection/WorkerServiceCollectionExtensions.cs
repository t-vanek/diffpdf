using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Worker.DependencyInjection;

public static class WorkerServiceCollectionExtensions
{
    /// <summary>Registers the in-memory job store, queue, and background processor.</summary>
    public static IServiceCollection AddDiffPdfWorker(this IServiceCollection services)
    {
        services.AddOptions<WorkerOptions>();
        services.AddSingleton<IJobStore, InMemoryJobStore>();
        services.AddSingleton<IComparisonJobQueue, ChannelComparisonJobQueue>();
        services.AddHostedService<ComparisonBackgroundService>();
        return services;
    }
}
