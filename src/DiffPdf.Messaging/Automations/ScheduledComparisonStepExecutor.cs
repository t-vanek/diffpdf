using DiffPdf.Core.Models;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Runs a scheduled comparison: for each enabled instance in the automation's scope, resolves the
/// effective configuration (Global → Branch → Instance) and enqueues a batch into the per-branch
/// sequential queue (<c>enqueueOnly</c>), so the dispatcher runs them one at a time. The cron/interval
/// cadence and the leader-gating are handled by the automation engine, exactly like the other steps.
/// </summary>
public sealed class ScheduledComparisonStepExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    IScopeConfigurationResolver resolver,
    IBatchLauncher launcher,
    ILogger<ScheduledComparisonStepExecutor> logger) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.ScheduledComparison;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        var targets = await AutomationScope.ResolveEnabledInstancesAsync(automation, branches, instances, ct);
        if (targets.Count == 0)
            return StepResult.Warning("No instances in scope.");

        int queued = 0;
        var skipped = new List<string>();
        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var eff = await resolver.ResolveForInstanceAsync(instance.BranchId, instance.Id, ct);
                var spec = LaunchSpec.FromEffective(eff, source: JobSource.Scheduler, sourceAutomationId: automation.Id);
                var result = await launcher.LaunchAsync(branchKey, instance.Key, spec, enqueueOnly: true, ct);
                if (result.Launched)
                    queued++;
                else
                    skipped.Add($"{branchKey}/{instance.Key}: {result.Outcome} ({result.Detail})");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled comparison: enqueue failed for {Branch}/{Instance}.", branchKey, instance.Key);
                skipped.Add($"{branchKey}/{instance.Key}: error ({ex.Message})");
            }
        }

        if (queued > 0)
            logger.LogInformation("Scheduled comparison '{Key}': enqueued {Queued}/{Total} instance(s).", automation.Key, queued, targets.Count);

        // "Nothing to compare" / "unreachable" are expected idle outcomes (Ok), not failures; a thrown error is a Warning.
        return queued == targets.Count
            ? StepResult.Ok($"enqueued {queued} instance(s).")
            : skipped.Any(s => s.Contains("error ("))
                ? StepResult.Warning($"enqueued {queued}/{targets.Count}; {skipped.Count} skipped:\n  - {string.Join("\n  - ", skipped)}")
                : StepResult.Ok($"enqueued {queued}/{targets.Count}; {skipped.Count} skipped (nothing to compare / unreachable).");
    }
}
