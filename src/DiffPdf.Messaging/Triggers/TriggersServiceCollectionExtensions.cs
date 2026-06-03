using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Triggers;

public static class TriggersServiceCollectionExtensions
{
    /// <summary>
    /// Registers the folder-watch trigger (scans the <c>new/</c> folder of each enabled watch in
    /// <see cref="DiffPdf.Persistence.IWatchStore"/> and launches a batch when a drop settles).
    /// Watches are runtime-managed via the API; the service stays idle while none are enabled.
    /// Requires <c>AddDiffPdfScheduling</c> (provides <see cref="Scheduling.IBatchLauncher"/>).
    /// </summary>
    public static IServiceCollection AddDiffPdfFolderWatch(this IServiceCollection services)
    {
        services.AddSingleton<IFolderManifestScanner, FolderManifestScanner>();
        services.AddHostedService<FolderWatchService>();
        return services;
    }
}
