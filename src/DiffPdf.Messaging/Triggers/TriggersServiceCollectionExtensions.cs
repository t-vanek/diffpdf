using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Triggers;

public static class TriggersServiceCollectionExtensions
{
    /// <summary>
    /// Registers the folder-watch trigger (scans configured <c>new/</c> folders and launches a
    /// batch when a drop settles), bound from the <c>Watches</c> configuration section. Safe to
    /// call always: when <c>Watches:Enabled</c> is false the hosted service exits immediately.
    /// Requires <c>AddDiffPdfScheduling</c> (provides <see cref="Scheduling.IBatchLauncher"/>).
    /// </summary>
    public static IServiceCollection AddDiffPdfFolderWatch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WatchOptions>(configuration.GetSection(WatchOptions.SectionName));
        services.AddSingleton<IFolderManifestScanner, FolderManifestScanner>();
        services.AddHostedService<FolderWatchService>();
        return services;
    }
}
