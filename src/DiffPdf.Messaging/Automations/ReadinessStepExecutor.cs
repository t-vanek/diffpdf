using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Verifies that the instances in the automation's scope are ready to compare: the base path is reachable
/// and both old/new input folders hold at least one PDF (the same gate the readiness endpoint reports).
/// </summary>
public sealed class ReadinessStepExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    IInstanceStructureService structure,
    ILogger<ReadinessStepExecutor> logger) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.Readiness;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        var targets = await AutomationScope.ResolveEnabledInstancesAsync(automation, branches, instances, ct);
        if (targets.Count == 0)
            return StepResult.Warning("No instances in scope.");

        var problems = new List<string>();
        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            InstanceStructureReport report;
            try
            {
                report = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Readiness step: inspect failed for {Branch}/{Instance}.", branchKey, instance.Key);
                problems.Add($"{branchKey}/{instance.Key}: inspect failed ({ex.Message})");
                continue;
            }

            if (!report.Reachable)
                problems.Add($"{branchKey}/{instance.Key}: base path unreachable ({report.Error})");
            else if (!report.HasComparableInputs)
                problems.Add($"{branchKey}/{instance.Key}: nothing to compare (old={report.OldPdfCount}, new={report.NewPdfCount})");
        }

        return problems.Count == 0
            ? StepResult.Ok($"{targets.Count} instance(s) ready.")
            : StepResult.Failed($"{problems.Count}/{targets.Count} instance(s) not ready:\n  - {string.Join("\n  - ", problems)}");
    }
}
