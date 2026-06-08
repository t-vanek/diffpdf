using DiffPdf.Application.Abstractions;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Runs;

/// <summary>One instance's launch result within a branch fan-out (keeps the launch outcome as the enum).</summary>
public sealed record InstanceRunOutcome(string InstanceKey, LaunchOutcome Outcome, Guid? JobId, string? Detail);

/// <summary>Result of fanning a run out across every enabled instance under a branch.</summary>
public sealed record BranchRunOutcome(string BranchKey, IReadOnlyList<InstanceRunOutcome> Instances);

/// <summary>
/// On-demand batch runs for a scope: launch one instance now, or fan out across every enabled instance of a
/// branch. Each launch uses the instance's effective (inherited) configuration. The fan-out and spec resolution
/// that used to sit in the trigger endpoints live here; the endpoint maps the launch outcome to HTTP.
/// </summary>
public interface IScopeRunService
{
    Task<LaunchResult> RunInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default);
    Task<BranchRunOutcome?> RunBranchAsync(string branchKey, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeRunService(
    IBranchStore branches, IInstanceStore instances, IScopeConfigurationResolver resolver, IBatchLauncher launcher) : IScopeRunService
{
    public async Task<LaunchResult> RunInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default)
    {
        var spec = await ResolveInstanceSpecAsync(branchKey, instanceKey, ct);
        return await launcher.LaunchAsync(branchKey, instanceKey, spec, ct: ct);
    }

    public async Task<BranchRunOutcome?> RunBranchAsync(string branchKey, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;

        var list = await instances.ListAsync(branch.Id, ct);
        var results = new List<InstanceRunOutcome>();
        foreach (var instance in list.Where(i => i.Enabled))
        {
            // Each instance launches with its own effective (inherited) configuration.
            var eff = await resolver.ResolveForInstanceAsync(branch.Id, instance.Id, ct);
            var r = await launcher.LaunchAsync(branchKey, instance.Key, LaunchSpec.FromEffective(eff), ct: ct);
            results.Add(new InstanceRunOutcome(instance.Key, r.Outcome, r.JobId, r.Detail));
        }
        return new BranchRunOutcome(branchKey, results);
    }

    /// <summary>
    /// Builds the launch spec for an on-demand instance run from its resolved effective configuration. Falls
    /// back to defaults when the branch/instance is unknown — the launcher then reports the not-found outcome.
    /// </summary>
    private async Task<LaunchSpec> ResolveInstanceSpecAsync(string branchKey, string instanceKey, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return LaunchSpec.Default;
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null) return LaunchSpec.Default;
        var eff = await resolver.ResolveForInstanceAsync(branch.Id, instance.Id, ct);
        return LaunchSpec.FromEffective(eff);
    }
}
