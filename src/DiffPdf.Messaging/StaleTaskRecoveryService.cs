using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
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
/// Periodic queue-maintenance sweep. Two jobs per tick: (1) requeues file-pair tasks whose worker lease
/// expired (crashed mid-comparison) and re-dispatches them, so a batch resumes instead of stalling; (2) heals
/// pairs left Queued under a now-terminal job (a cancel that raced the per-pair dispatch), which would otherwise
/// linger as "pending" comparisons until the next restart. Recovery is safe: re-dispatched pairs are idempotent
/// (claim + complete-once), and a finished job's CompleteAsync is a no-op.
/// </summary>
public sealed class StaleTaskRecoveryService(
    IServiceScopeFactory scopeFactory,
    IAutomationHeartbeat heartbeat,
    IOptions<WorkerOptions> options,
    ILogger<StaleTaskRecoveryService> logger) : BackgroundService
{
    private const string ServiceName = "stale-recovery";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.StaleRecoveryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var taskStore = scope.ServiceProvider.GetRequiredService<IFilePairTaskStore>();
                var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

                var recovered = await taskStore.RequeueStaleAsync(stoppingToken);
                foreach (var (jobId, taskId) in recovered)
                    await bus.PublishAsync(new CompareFilePair(jobId, taskId));

                if (recovered.Count > 0)
                {
                    // Flag the affected jobs so the client shows an "Obnoveno" chip; the flag rides the next
                    // natural progress push when each re-dispatched pair completes (and a toast fires there).
                    var recoveredJobIds = recovered.Select(r => r.JobId).Distinct().ToList();
                    await jobStore.MarkRecoveredAsync(recoveredJobIds, stoppingToken);
                    logger.LogInformation("Recovered {Count} stale file-pair task(s)", recovered.Count);

                    // Recovery interventions belong in the event trail — they mean a worker died mid-comparison.
                    if (scope.ServiceProvider.GetService<ISystemEventLog>() is { } events)
                        foreach (var jobId in recoveredJobIds)
                        {
                            var job = await jobStore.GetAsync(jobId, stoppingToken);
                            await events.AppendAsync(new SystemEvent
                            {
                                Type = SystemEventTypes.JobRecovered,
                                Severity = SystemEventSeverity.Warning,
                                BranchKey = job?.BranchKey,
                                InstanceKey = job?.InstanceKey,
                                JobId = jobId,
                                Message = job is null
                                    ? "Porovnání bylo po přerušení obnoveno (vypršela zámková lhůta workeru)."
                                    : $"Porovnání {job.BranchKey}/{job.InstanceKey} bylo po přerušení obnoveno (vypršela zámková lhůta workeru).",
                                Detail = $"{recovered.Count(r => r.JobId == jobId)} párů vráceno do fronty.",
                            }, stoppingToken);
                        }
                }

                // Heal pairs stranded Queued under a now-terminal (Cancelled/Failed/Completed) job. Runs AFTER the
                // requeue above so a stale-Running pair of a terminal job is first returned to Queued, then skipped
                // here in the same tick. No-op on the in-memory store (no cross-store job view); effective on SQL.
                int healed = await taskStore.SkipPendingForTerminalJobsAsync(stoppingToken);
                if (healed > 0)
                    logger.LogInformation("Skipped {Count} stranded Queued pair(s) under terminal jobs", healed);

                // Not leader-gated — runs on every replica — so each tick is "active work".
                heartbeat.Record(ServiceName, leaderActive: true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Stale task recovery pass failed");
            }
        }
    }
}
