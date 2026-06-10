using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Resolves the set of enabled instances an automation applies to from its <see cref="AutomationScopeKind"/>
/// (Instance / Branch / Global). Shared by the step executors that act per instance (readiness, scheduled runs).
/// </summary>
internal static class AutomationScope
{
    public static async Task<IReadOnlyList<(string BranchKey, ComparisonInstance Instance)>> ResolveEnabledInstancesAsync(
        Automation automation, IBranchStore branches, IInstanceStore instances, CancellationToken ct)
    {
        var result = new List<(string, ComparisonInstance)>();

        switch (automation.ScopeKind)
        {
            case AutomationScopeKind.Instance when automation.BranchKey is { } bk && automation.InstanceKey is { } ik:
            {
                var branch = await branches.GetByKeyAsync(bk, ct);
                if (branch is null) break;
                var instance = await instances.GetByKeyAsync(branch.Id, ik, ct);
                if (instance is { Enabled: true })
                    result.Add((bk, instance));
                break;
            }
            case AutomationScopeKind.Branch when automation.BranchKey is { } bk:
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
