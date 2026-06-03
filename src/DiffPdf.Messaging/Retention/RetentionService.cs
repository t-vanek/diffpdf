using DiffPdf.Core.Abstractions;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Retention;

/// <summary>
/// Periodically deletes the on-disk artifacts (the <c>reports/{jobId}</c> folder) of finished jobs
/// older than the retention window. DB rows and schedule run history are kept — only the bulky
/// report PDFs are pruned. Leader-gated (shares the <see cref="AutomationLeader"/> lease, so only
/// one replica prunes) and idempotent (each job is marked pruned and skipped on later passes).
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    IOptions<RetentionOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    private const string ServiceName = "retention";
    private bool _wasLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Artifact retention disabled.");
            return;
        }

        logger.LogInformation("Artifact retention started: prune reports of jobs older than {Days} d every {Hours} h.",
            opts.RetentionDays, opts.IntervalHours);

        using var timer = new PeriodicTimer(opts.Interval);
        do
        {
            try
            {
                await TickAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Retention pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(RetentionOptions opts, CancellationToken ct)
    {
        // Only the automation leader prunes, so replicas don't fight over the same folders.
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        if (isLeader != _wasLeader)
        {
            logger.LogInformation(isLeader
                ? "This replica now runs artifact retention."
                : "This replica no longer runs artifact retention.");
            _wasLeader = isLeader;
        }
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var paths = scope.ServiceProvider.GetRequiredService<IJobStoragePathProvider>();

        var cutoff = DateTimeOffset.UtcNow - opts.MaxAge;
        var prunable = await jobs.ListPrunableArtifactsAsync(cutoff, opts.MaxPerTick, ct);
        if (prunable.Count == 0)
            return;

        int pruned = 0;
        foreach (var job in prunable)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string root = paths.GetJobRoot(job);
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                await jobs.MarkArtifactsPrunedAsync(job.Id, DateTimeOffset.UtcNow, ct);
                pruned++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to prune artifacts for job {JobId}.", job.Id);
            }
        }

        if (pruned > 0)
            logger.LogInformation("Retention pruned artifacts of {Count} job(s) completed before {Cutoff:u}.", pruned, cutoff);
    }
}
