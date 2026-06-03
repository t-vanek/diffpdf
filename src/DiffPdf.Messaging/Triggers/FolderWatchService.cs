using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Triggers;

/// <summary>
/// Polls each enabled folder-watch's instance <c>new/</c> folder and launches a batch once a drop
/// has settled (see <see cref="WatchState"/>). Watches are runtime-managed in <see cref="IWatchStore"/>
/// and re-read every tick, so adding / removing / disabling a watch via the API takes effect within
/// one tick — no restart. Polling (rather than FileSystemWatcher) works uniformly for local, mounted
/// and UNC/CIFS shares.
/// </summary>
/// <remarks>
/// Multi-replica safe: each tick first acquires/renews the shared <see cref="AutomationLeader"/>
/// lease; only the leading replica scans + launches. Per-folder dedupe state is in-memory, so a
/// leadership change may re-trigger the most recent drop once; concurrent replicas never double-launch.
/// </remarks>
public sealed class FolderWatchService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    ILogger<FolderWatchService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<Guid, WatchState> _states = new();
    private bool _wasLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Folder-watch started (DB-backed, {Poll}s poll).", PollInterval.TotalSeconds);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Folder-watch tick failed.");
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        if (isLeader != _wasLeader)
        {
            logger.LogInformation(isLeader
                ? "This replica is now the automation leader (folder-watch active)."
                : "This replica is no longer the automation leader (folder-watch idle).");
            _wasLeader = isLeader;
        }
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var watchStore = sp.GetRequiredService<IWatchStore>();
        var instances = sp.GetRequiredService<IInstanceStore>();
        var scanner = sp.GetRequiredService<IFolderManifestScanner>();
        var launcher = sp.GetRequiredService<IBatchLauncher>();

        var watches = await watchStore.ListEnabledAsync(ct);
        var live = new HashSet<Guid>();

        foreach (var watch in watches)
        {
            live.Add(watch.Id);
            ct.ThrowIfCancellationRequested();
            try
            {
                var instance = await instances.GetByKeyAsync(watch.BranchId, watch.InstanceKey, ct);
                if (instance is null || !instance.Enabled)
                    continue;

                var manifest = scanner.ScanNewFolder(instance.BasePath, instance.CredentialProfile, ct);
                if (manifest is null)
                    continue; // unreachable this tick; retry next time

                var state = _states.TryGetValue(watch.Id, out var s) ? s : (_states[watch.Id] = new WatchState());
                if (state.Observe(manifest, DateTimeOffset.UtcNow, watch.Stability))
                {
                    logger.LogInformation("Folder-watch: stable drop in {Branch}/{Instance} ({Count} file(s)); launching.",
                        watch.BranchKey, watch.InstanceKey, manifest.FileCount);
                    var result = await launcher.LaunchAsync(watch.BranchKey, watch.InstanceKey, LaunchSpec.Default, ct);
                    if (result.JobId is not null)
                        await watchStore.TouchLastTriggeredAsync(watch.Id, DateTimeOffset.UtcNow, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Folder-watch pass failed for {Branch}/{Instance}.", watch.BranchKey, watch.InstanceKey);
            }
        }

        // Drop debounce state for watches that vanished (deleted or disabled at runtime).
        foreach (var goneId in _states.Keys.Where(id => !live.Contains(id)).ToList())
            _states.Remove(goneId);
    }
}
