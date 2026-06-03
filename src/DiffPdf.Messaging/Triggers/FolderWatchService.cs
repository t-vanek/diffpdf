using DiffPdf.Core.Abstractions;
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
/// Multi-replica safe: each tick first acquires/renews the shared <see cref="AutomationLeader"/>
/// lease via <see cref="ILeaderElection"/>; only the leading replica scans + launches (the in-memory
/// fallback always leads). The per-folder dedupe state is in-memory, so a leadership change may
/// re-trigger the most recent drop once — concurrent replicas never double-launch.
/// </remarks>
public sealed class FolderWatchService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IOptions<WatchOptions> options,
    ILogger<FolderWatchService> logger) : BackgroundService
{
    private bool _wasLeader;

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
            // Only the automation leader scans + launches, so a drop fires once across replicas.
            bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, stoppingToken);
            if (isLeader != _wasLeader)
            {
                logger.LogInformation(isLeader
                    ? "This replica is now the automation leader (folder-watch active)."
                    : "This replica is no longer the automation leader (folder-watch idle).");
                _wasLeader = isLeader;
            }
            if (!isLeader)
                continue;

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
                        await sp.GetRequiredService<IBatchLauncher>().LaunchAsync(watch.BranchKey, watch.InstanceKey, LaunchSpec.Default, stoppingToken);
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
