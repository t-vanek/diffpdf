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
        var targets = await ResolveTargetsAsync(check, ct);
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

    private async Task<IReadOnlyList<(string BranchKey, ComparisonInstance Instance)>> ResolveTargetsAsync(ControlCheck check, CancellationToken ct)
    {
        var result = new List<(string, ComparisonInstance)>();

        switch (check.ScopeKind)
        {
            case CheckScopeKind.Instance when check.BranchKey is { } bk && check.InstanceKey is { } ik:
            {
                var branch = await branches.GetByKeyAsync(bk, ct);
                if (branch is null) break;
                var instance = await instances.GetByKeyAsync(branch.Id, ik, ct);
                if (instance is { Enabled: true })
                    result.Add((bk, instance));
                break;
            }
            case CheckScopeKind.Branch when check.BranchKey is { } bk:
            {
                var branch = await branches.GetByKeyAsync(bk, ct);
                if (branch is null) break;
                foreach (var instance in await instances.ListAsync(branch.Id, ct))
                    if (instance.Enabled)
                        result.Add((bk, instance));
                break;
            }
            default: // Global
            {
                foreach (var branch in await branches.ListAsync(ct))
                {
                    if (!branch.Enabled) continue;
                    foreach (var instance in await instances.ListAsync(branch.Id, ct))
                        if (instance.Enabled)
                            result.Add((branch.Key, instance));
                }
                break;
            }
        }

        return result;
    }
}
