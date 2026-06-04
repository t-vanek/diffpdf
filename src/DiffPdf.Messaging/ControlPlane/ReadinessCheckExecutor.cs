using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Verifies that the instances in the check's scope are ready to compare: the base path is reachable
/// and both old/new input folders hold at least one PDF (the same gate the readiness endpoint reports).
/// </summary>
public sealed class ReadinessCheckExecutor(
    IBranchStore branches,
    IInstanceStore instances,
    IInstanceStructureService structure,
    ILogger<ReadinessCheckExecutor> logger) : IControlCheckExecutor
{
    public CheckType Type => CheckType.Readiness;

    public async Task<CheckResult> ExecuteAsync(ControlCheck check, CancellationToken ct)
    {
        var targets = await ControlCheckScope.ResolveEnabledInstancesAsync(check, branches, instances, ct);
        if (targets.Count == 0)
            return CheckResult.Warning("No instances in scope.");

        var problems = new List<string>();
        foreach (var (branchKey, instance) in targets)
        {
            ct.ThrowIfCancellationRequested();
            InstanceStructureReport report;
            try
            {
                report = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Readiness check: inspect failed for {Branch}/{Instance}.", branchKey, instance.Key);
                problems.Add($"{branchKey}/{instance.Key}: inspect failed ({ex.Message})");
                continue;
            }

            if (!report.Reachable)
                problems.Add($"{branchKey}/{instance.Key}: base path unreachable ({report.Error})");
            else if (!report.HasComparableInputs)
                problems.Add($"{branchKey}/{instance.Key}: nothing to compare (old={report.OldPdfCount}, new={report.NewPdfCount})");
        }

        return problems.Count == 0
            ? CheckResult.Ok($"{targets.Count} instance(s) ready.")
            : CheckResult.Failed($"{problems.Count}/{targets.Count} instance(s) not ready:\n  - {string.Join("\n  - ", problems)}");
    }
}
