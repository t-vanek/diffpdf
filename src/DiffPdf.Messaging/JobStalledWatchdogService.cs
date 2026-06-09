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

    // Jobs already alerted as stalled, so a still-stalled job is not re-notified every tick. Mutated only on the
    // single-threaded leader tick. In-memory: a leadership change re-alerts currently-stalled jobs once (accepted).
    private readonly HashSet<Guid> _alerted = [];

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

        var now = DateTimeOffset.UtcNow;
        var running = await jobs.ListAsync(new JobListQuery { Status = JobStatus.Running, Limit = 1000 }, ct);
        var stalled = StuckJobDetector.FindStalled(running, now, threshold);

        // Notify once per newly-stalled job (dedup against the in-memory set); then replace the set with the
        // current stalled ids — drops recovered jobs (no re-alert) and keeps still-stalled ones silent.
        foreach (var job in stalled)
        {
            if (!_alerted.Add(job.Id))
                continue;
            var lastProgress = StuckJobDetector.LastProgress(job);
            logger.LogWarning(
                "Job {JobId} ({Branch}/{Instance}) stalled: {Processed}/{Total} pairs, no progress for {Minutes:0} min.",
                job.Id, job.BranchKey, job.InstanceKey, job.ProcessedCount, job.TotalCount, (now - lastProgress).TotalMinutes);
            await dispatcher.DispatchAsync(new JobStalledNotification(
                job.Id, job.BranchKey, job.InstanceKey, job.ProcessedCount, job.TotalCount,
                now - lastProgress, lastProgress, now), ct);
        }
        _alerted.IntersectWith(stalled.Select(j => j.Id)); // forget recovered jobs so they can alert again later

        metrics.RecordStuckJobs(stalled.Count);
        metrics.RecordActiveTasks(await tasks.CountActiveAsync(ct));
    }
}
