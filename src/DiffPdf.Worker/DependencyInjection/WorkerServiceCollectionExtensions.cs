using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Worker.DependencyInjection;

public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers worker-side infrastructure shared by the job handler: options,
    /// worker identity, storage path provider/provisioner and a default
    /// (no-op) progress publisher. Job processing itself runs via the messaging
    /// handler (Wolverine), not an in-process queue.
    /// </summary>
    public static IServiceCollection AddDiffPdfWorker(this IServiceCollection services)
    {
        services.AddOptions<WorkerOptions>();
        services.AddOptions<StorageOptions>();

        services.AddSingleton<IWorkerInstanceIdProvider, WorkerInstanceIdProvider>();
        services.AddSingleton<IJobStoragePathProvider, FileSystemStoragePathProvider>();
        services.AddSingleton<IStorageProvisioner, FileSystemStorageProvisioner>();

        // Replaced by a real transport (SignalR) when realtime progress is enabled.
        services.TryAddProgressPublisher();

        return services;
    }

    private static void TryAddProgressPublisher(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IJobProgressPublisher)))
            services.AddSingleton<IJobProgressPublisher, NullJobProgressPublisher>();
    }
}
