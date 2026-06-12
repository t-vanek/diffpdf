using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging;

/// <summary>
/// Leader-gated watchdog that periodically scans Running comparison jobs and alerts (notify-only) on any that
/// stalled mid-comparison — finished indexing but stopped completing pairs past the configured window — and
/// feeds the stuck-job + active-task backlog gauges. Mirrors <see cref="Scheduling.BranchQueueDispatcherService"/>'s
/// leader-gating (same <see cref="AutomationLeader"/> lease). It never fails or cancels a job: a comparison-phase
/// stall can still self-recover via the stale-task requeue, so the watchdog only surfaces it for an operator.
/// </summary>
public sealed class JobStalledWatchdogService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    DiffPdfMetrics metrics,
    IOptions<StuckJobWatchdogOptions> options,
    ILogger<JobStalledWatchdogService> logger) : BackgroundService
{
    private const string ServiceName = "stuck-job-watchdog";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Stuck-job watchdog disabled.");
            return;
        }

        logger.LogInformation("Stuck-job watchdog started ({Interval}s tick, {Threshold} min stall threshold).",
            opts.Interval.TotalSeconds, opts.StallThreshold.TotalMinutes);
        using var timer = new PeriodicTimer(opts.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(opts.StallThreshold, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Stuck-job watchdog tick failed.");
            }
        }
    }

    private async Task TickAsync(TimeSpan threshold, CancellationToken ct)
    {
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var tasks = scope.ServiceProvider.GetRequiredService<IFilePairTaskStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
        var automationEvents = scope.ServiceProvider.GetRequiredService<IAutomationEventSink>();
        var eventStore = scope.ServiceProvider.GetRequiredService<ISystemEventStore>();
        var systemEvents = scope.ServiceProvider.GetRequiredService<ISystemEventLog>();

        var now = DateTimeOffset.UtcNow;
        var running = await jobs.ListAsync(new JobListQuery { Status = JobStatus.Running, Limit = 1000 }, ct);
        var stalled = StuckJobDetector.FindStalled(running, now, threshold);

        // Notify once per stall: the dedup is the event log itself — a job.stalled event newer than the job's
        // last progress means this stall was already announced. Durable, so a restart or a leadership change
        // no longer re-alerts (the old in-memory set did); a job that progresses and stalls again alerts again
        // (its last-progress timestamp moved past the recorded event). If the event append fails (best-effort),
        // the worst case is one duplicate alert on the next tick.
        foreach (var job in stalled)
        {
            var lastProgress = StuckJobDetector.LastProgress(job);
            if (await eventStore.ExistsForJobAsync(SystemEventTypes.JobStalled, job.Id, lastProgress, ct))
                continue;

            logger.LogWarning(
                "Job {JobId} ({Branch}/{Instance}) stalled: {Processed}/{Total} pairs, no progress for {Minutes:0} min.",
                job.Id, job.BranchKey, job.InstanceKey, job.ProcessedCount, job.TotalCount, (now - lastProgress).TotalMinutes);
            await systemEvents.AppendAsync(new SystemEvent
            {
                Type = SystemEventTypes.JobStalled,
                Severity = SystemEventSeverity.Warning,
                BranchKey = job.BranchKey,
                InstanceKey = job.InstanceKey,
                JobId = job.Id,
                Message = $"Porovnání {job.BranchKey}/{job.InstanceKey} se zaseklo: {job.ProcessedCount}/{job.TotalCount} párů, bez postupu {(now - lastProgress).TotalMinutes:0} min.",
            }, ct);
            await dispatcher.DispatchAsync(new JobStalledNotification(
                job.Id, job.BranchKey, job.InstanceKey, job.ProcessedCount, job.TotalCount,
                now - lastProgress, lastProgress, now), ct);
            // Stalls also fire event-triggered automations (JobStalled is offered as a spouštěč); the
            // launching automation is excluded so it cannot re-trigger itself off its own stalled batch.
            await automationEvents.PublishAsync(
                NotificationEvent.JobStalled, job.BranchKey, job.InstanceKey,
                $"{job.BranchKey}/{job.InstanceKey}: {job.ProcessedCount}/{job.TotalCount} párů, bez postupu {(now - lastProgress).TotalMinutes:0} min",
                sourceAutomationId: job.SourceAutomationId,
                chainDepth: job.SourceAutomationId is null ? 0 : 1, ct);
        }

        metrics.RecordStuckJobs(stalled.Count);
        metrics.RecordActiveTasks(await tasks.CountActiveAsync(ct));
    }
}
