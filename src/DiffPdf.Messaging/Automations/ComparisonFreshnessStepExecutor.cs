using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Monitors data freshness: for each enabled instance in the automation's scope it finds the most recent
/// <see cref="JobStatus.Completed"/> comparison and checks how old it is. A pipeline that silently stopped
/// producing results is otherwise invisible — nothing fails, results just go stale. Read-only.
/// Thresholds (hours): older than <c>failAgeHours</c> → Failed, older than <c>warnAgeHours</c> → Warning;
/// an instance that never produced a successful comparison is a Warning. Defaults: <c>warnAgeHours</c> 24,
/// <c>failAgeHours</c> 48.
/// </summary>
public sealed class ComparisonFreshnessStepExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    IJobStore jobs) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.ComparisonFreshness;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        int warnAgeHours = StepParameters.GetInt(step, "warnAgeHours", 24);
        int failAgeHours = StepParameters.GetInt(step, "failAgeHours", 48);

        var targets = await AutomationScope.ResolveEnabledInstancesAsync(automation, branches, instances, ct);
        if (targets.Count == 0)
            return StepResult.Warning("No instances in scope.");

        var now = DateTimeOffset.UtcNow;
        var stale = new List<string>();
        var failing = new List<string>();

        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var latest = await jobs.ListSummariesAsync(
                new JobListQuery { BranchKey = branchKey, InstanceKey = instance.Key, Status = JobStatus.Completed, Limit = 1 },
                ct);

            if (latest.Count == 0 || latest[0].CompletedAt is not { } completedAt)
            {
                stale.Add($"{branchKey}/{instance.Key}: never");
                continue;
            }

            double ageHours = (now - completedAt).TotalHours;
            if (ageHours >= failAgeHours)
                failing.Add($"{branchKey}/{instance.Key}: {ageHours:0.#} h");
            else if (ageHours >= warnAgeHours)
                stale.Add($"{branchKey}/{instance.Key}: {ageHours:0.#} h");
        }

        if (failing.Count > 0)
            return StepResult.Failed(
                $"{failing.Count} instance(s) stale beyond {failAgeHours} h:\n  - {string.Join("\n  - ", failing.Concat(stale))}");
        if (stale.Count > 0)
            return StepResult.Warning(
                $"{stale.Count} instance(s) stale beyond {warnAgeHours} h:\n  - {string.Join("\n  - ", stale)}");

        return StepResult.Ok($"all {targets.Count} instance(s) fresh (within {warnAgeHours} h).");
    }
}
