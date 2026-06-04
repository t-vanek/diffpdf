using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Resolves the set of enabled instances a control check applies to from its <see cref="CheckScopeKind"/>
/// (Instance / Branch / Global). Shared by the executors that act per instance (readiness, scheduled runs).
/// </summary>
internal static class ControlCheckScope
{
    public static async Task<IReadOnlyList<(string BranchKey, ComparisonInstance Instance)>> ResolveEnabledInstancesAsync(
        ControlCheck check, IBranchStore branches, IInstanceStore instances, CancellationToken ct)
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
