using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Re-enqueues a fresh comparison for in-scope instances whose most recent batch ended badly — recovering
/// from transient failures (renderer briefly down, share momentarily unreachable). For each enabled instance
/// it looks at the latest <b>terminal</b> job: a job-level <see cref="JobStatus.Failed"/> is always re-run;
/// a <see cref="JobStatus.Completed"/> batch with pair-level errors is re-run only when
/// <c>includePairErrors</c> is true (off by default, since a genuinely broken PDF would otherwise loop). An
/// instance with an in-flight job is skipped (its run is the throttle). Only jobs finished within
/// <c>withinHours</c> are considered, so stale failures are not retried forever. Enqueues into the per-branch
/// sequential queue, like a scheduled comparison. Defaults: <c>includePairErrors</c> false, <c>withinHours</c> 24.
/// </summary>
public sealed class ReRunFailedStepExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    IJobStore jobs,
    IScopeConfigurationResolver resolver,
    IBatchLauncher launcher,
    ILogger<ReRunFailedStepExecutor> logger) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.ReRunFailed;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        bool includePairErrors = StepParameters.GetBool(step, "includePairErrors", false);
        int withinHours = StepParameters.GetInt(step, "withinHours", 24);

        var targets = await AutomationScope.ResolveEnabledInstancesAsync(automation, branches, instances, ct);
        if (targets.Count == 0)
            return StepResult.Warning("No instances in scope.");

        var now = DateTimeOffset.UtcNow;
        int requeued = 0;
        var skippedErrors = new List<string>();

        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var latest = await jobs.ListSummariesAsync(
                    new JobListQuery { BranchKey = branchKey, InstanceKey = instance.Key, Limit = 1 }, ct);
                if (latest.Count == 0)
                    continue;

                var job = latest[0];

                // An in-flight job is the natural throttle — never stack a re-run on top of one.
                if (job.Status is JobStatus.Draft or JobStatus.Queued or JobStatus.Running or JobStatus.Paused)
                    continue;

                bool failedJob = job.Status == JobStatus.Failed;
                bool erroredBatch = includePairErrors && job.Status == JobStatus.Completed && job.Errors is > 0;
                if (!failedJob && !erroredBatch)
                    continue;

                var finishedAt = job.CompletedAt ?? job.CreatedAt;
                if ((now - finishedAt).TotalHours > withinHours)
                    continue;

                var eff = await resolver.ResolveForInstanceAsync(instance.BranchId, instance.Id, ct);
                var spec = LaunchSpec.FromEffective(eff, source: JobSource.Scheduler, sourceAutomationId: automation.Id);
                var result = await launcher.LaunchAsync(branchKey, instance.Key, spec, enqueueOnly: true, ct);
                if (result.Launched)
                {
                    requeued++;
                    logger.LogInformation(
                        "Re-run: re-enqueued {Branch}/{Instance} after {Reason}.",
                        branchKey, instance.Key, failedJob ? "job failure" : "pair errors");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Re-run: failed to evaluate/enqueue {Branch}/{Instance}.", branchKey, instance.Key);
                skippedErrors.Add($"{branchKey}/{instance.Key}: {ex.Message}");
            }
        }

        if (skippedErrors.Count > 0)
            return StepResult.Warning(
                $"re-enqueued {requeued}; {skippedErrors.Count} error(s):\n  - {string.Join("\n  - ", skippedErrors)}");

        return StepResult.Ok(requeued == 0 ? "nothing to re-run." : $"re-enqueued {requeued} instance(s).");
    }
}
