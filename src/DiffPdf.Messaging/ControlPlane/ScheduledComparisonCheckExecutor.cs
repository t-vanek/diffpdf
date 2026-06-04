using DiffPdf.Core.Models;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Runs a scheduled comparison: for each enabled instance in the check's scope, resolves the effective
/// configuration (Global → Branch → Instance) and enqueues a batch into the per-branch sequential queue
/// (<c>enqueueOnly</c>), so the dispatcher runs them one at a time. The cron/interval cadence and the
/// leader-gating are handled by the control-plane runner, exactly like the other checks.
/// </summary>
public sealed class ScheduledComparisonCheckExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    IScopeConfigurationResolver resolver,
    IBatchLauncher launcher,
    ILogger<ScheduledComparisonCheckExecutor> logger) : IControlCheckExecutor
{
    public CheckType Type => CheckType.ScheduledComparison;

    public async Task<CheckResult> ExecuteAsync(ControlCheck check, CancellationToken ct)
    {
        var targets = await ControlCheckScope.ResolveEnabledInstancesAsync(check, branches, instances, ct);
        if (targets.Count == 0)
            return CheckResult.Warning("No instances in scope.");

        int queued = 0;
        var skipped = new List<string>();
        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var eff = await resolver.ResolveForInstanceAsync(instance.BranchId, instance.Id, ct);
                var spec = LaunchSpec.FromEffective(eff, source: JobSource.Scheduler);
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
            logger.LogInformation("Scheduled comparison '{Key}': enqueued {Queued}/{Total} instance(s).", check.Key, queued, targets.Count);

        // "Nothing to compare" / "unreachable" are expected idle outcomes (Ok), not failures; a thrown error is a Warning.
        return queued == targets.Count
            ? CheckResult.Ok($"enqueued {queued} instance(s).")
            : skipped.Any(s => s.Contains("error ("))
                ? CheckResult.Warning($"enqueued {queued}/{targets.Count}; {skipped.Count} skipped:\n  - {string.Join("\n  - ", skipped)}")
                : CheckResult.Ok($"enqueued {queued}/{targets.Count}; {skipped.Count} skipped (nothing to compare / unreachable).");
    }
}
