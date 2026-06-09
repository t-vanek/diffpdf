using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DiffPdf.Messaging;

/// <summary>
/// Bookends a worker's lifetime so a comparison interrupted by shutdown/crash recovers in seconds instead of
/// waiting out the ~12-min file-pair lease (<see cref="WorkerOptions.FilePairLease"/>).
/// <para>
/// <b>Shutdown</b> (graceful restart/deploy) — <see cref="StopAsync"/> returns THIS process's in-flight pairs
/// (Running → Queued, filtered by worker id, multi-replica-safe) to the queue. It deliberately does NOT
/// republish: during shutdown a publish races Wolverine's transport teardown and can be lost. The requeue is
/// what matters — Wolverine redelivers the still-persisted <see cref="CompareFilePair"/> envelopes on the next
/// start and the now-Queued task claims cleanly, so the pair re-compares immediately.
/// </para>
/// <para>
/// <b>Startup</b> (after a hard crash, when nothing ran <see cref="StopAsync"/>) — <see cref="StartAsync"/>,
/// when <see cref="WorkerOptions.ReclaimOrphansOnStartup"/> is set, returns ALL Running pairs to the queue and
/// republishes them. The only worker just started, so any Running task is an orphan of the dead process.
/// Publishing here is safe (no teardown race).
/// </para>
/// Both paths are best-effort and swallow failures so they can never hang shutdown or block startup; the
/// stale-lease sweeper (<see cref="StaleTaskRecoveryService"/>) and the complete-once guard remain the backstops,
/// the latter bounding any double-run from a recovery to wasted compute — never a corrupted count.
/// </summary>
public sealed class WorkerLifecycleService(
    IServiceScopeFactory scopeFactory,
    IWorkerInstanceIdProvider workerInstance,
    IOptions<WorkerOptions> options,
    ILogger<WorkerLifecycleService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ReclaimOrphansOnStartup)
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var taskStore = scope.ServiceProvider.GetRequiredService<IFilePairTaskStore>();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

            // Hard-crash recovery: this is the only worker and it just started, so every Running task is an
            // orphan left mid-comparison by the previous process. Return them to the queue and re-dispatch.
            var reclaimed = await taskStore.RequeueRunningTasksAsync(lockedBy: null, cancellationToken);
            foreach (var (jobId, taskId) in reclaimed)
                await bus.PublishAsync(new CompareFilePair(jobId, taskId));

            if (reclaimed.Count > 0)
                logger.LogInformation("Reclaimed {Count} orphaned in-flight pair(s) at startup", reclaimed.Count);
        }
        catch (Exception ex)
        {
            // Never block startup — the stale-lease sweeper still reclaims these within the lease window.
            logger.LogWarning(ex, "Startup orphan reclaim failed; relying on stale-lease recovery");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var taskStore = scope.ServiceProvider.GetRequiredService<IFilePairTaskStore>();

            // Release ONLY this process's in-flight pairs (multi-replica-safe). No republish: the transport is
            // tearing down, so a publish here races teardown and may be lost. The requeue-to-Queued is enough —
            // Wolverine redelivers the persisted CompareFilePair on the next start and it claims the Queued task.
            var released = await taskStore.RequeueRunningTasksAsync(workerInstance.WorkerInstanceId, cancellationToken);
            if (released.Count > 0)
                logger.LogInformation("Released {Count} in-flight pair(s) on shutdown for fast restart recovery", released.Count);
        }
        catch (Exception ex)
        {
            // Never hang shutdown — the ~12-min lease + stale-lease sweeper remain the floor.
            logger.LogWarning(ex, "Shutdown in-flight release failed; relying on stale-lease recovery");
        }
    }
}
