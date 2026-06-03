using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Triggers;

/// <summary>
/// Polls each watched instance's <c>new/</c> folder and launches a batch once a drop has
/// settled (see <see cref="WatchState"/>). Polling (rather than FileSystemWatcher) works
/// uniformly for local, mounted and UNC/CIFS shares.
/// </summary>
/// <remarks>
/// Single-process safe (in-memory per-folder state dedupes drops). Multiple API replicas
/// would each watch and could double-launch — multi-replica single-fire (a DB leader-lease)
/// is the documented follow-up, shared with the scheduler.
/// </remarks>
public sealed class FolderWatchService(
    IServiceScopeFactory scopeFactory,
    IOptions<WatchOptions> options,
    ILogger<FolderWatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var watches = opts.Folders.Where(w => w.Enabled).ToList();
        if (!opts.Enabled || watches.Count == 0)
        {
            logger.LogInformation("Folder-watch disabled or no folders configured.");
            return;
        }

        var states = watches.Select(_ => new WatchState()).ToArray();
        logger.LogInformation("Folder-watch started for {Count} folder(s), polling every {Poll}s.", watches.Count, opts.PollSeconds);

        using var timer = new PeriodicTimer(opts.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            for (int i = 0; i < watches.Count; i++)
            {
                var watch = watches[i];
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sp = scope.ServiceProvider;

                    var branch = await sp.GetRequiredService<IBranchStore>().GetByKeyAsync(watch.BranchKey, stoppingToken);
                    var instance = branch is null
                        ? null
                        : await sp.GetRequiredService<IInstanceStore>().GetByKeyAsync(branch.Id, watch.InstanceKey, stoppingToken);
                    if (instance is null || !instance.Enabled)
                        continue;

                    var manifest = sp.GetRequiredService<IFolderManifestScanner>()
                        .ScanNewFolder(instance.BasePath, instance.CredentialProfile, stoppingToken);
                    if (manifest is null)
                        continue; // unreachable this tick; retry next time

                    if (states[i].Observe(manifest, DateTimeOffset.UtcNow, watch.Stability))
                    {
                        logger.LogInformation("Folder-watch: stable drop in {Branch}/{Instance} ({Count} file(s)); launching.",
                            watch.BranchKey, watch.InstanceKey, manifest.FileCount);
                        await sp.GetRequiredService<IBatchLauncher>().LaunchAsync(watch.BranchKey, watch.InstanceKey, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Folder-watch pass failed for {Branch}/{Instance}.", watch.BranchKey, watch.InstanceKey);
                }
            }
        }
    }
}
