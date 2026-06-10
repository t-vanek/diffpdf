using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Configuration;

/// <summary>The current scheduled-run setting for a scope: whether it is enabled and its cron expression.</summary>
public sealed record ScheduleView(bool Enabled, string? Cron);

/// <summary>
/// Manages the per-branch / per-instance scheduled-comparison setting exposed by the gear "Konfigurace"
/// dialog, backed by a single-step <see cref="AutomationStepType.ScheduledComparison"/> automation with a
/// stable key (<c>sched-{branch}</c> or <c>sched-{branch}-{instance}</c>). Enabling upserts the
/// automation (seeding its <c>NextRunAt</c>); disabling deletes it. The automation engine then fires it on
/// its cron cadence like any other automation.
/// </summary>
public interface IScheduleService
{
    /// <summary>The branch's schedule, or null if the branch does not exist.</summary>
    Task<ScheduleView?> GetBranchScheduleAsync(string branchKey, CancellationToken ct = default);

    /// <summary>The instance's schedule, or null if the branch/instance does not exist.</summary>
    Task<ScheduleView?> GetInstanceScheduleAsync(string branchKey, string instanceKey, CancellationToken ct = default);

    /// <summary>Enable (upsert) or disable (delete) the branch schedule. Null if the branch does not exist.</summary>
    Task<ScheduleView?> SetBranchScheduleAsync(string branchKey, bool enabled, string cron, CancellationToken ct = default);

    /// <summary>Enable (upsert) or disable (delete) the instance schedule. Null if the branch/instance does not exist.</summary>
    Task<ScheduleView?> SetInstanceScheduleAsync(string branchKey, string instanceKey, bool enabled, string cron, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScheduleService(IAutomationStore automations, IBranchStore branches, IInstanceStore instances) : IScheduleService
{
    internal static string BranchKeyFor(string branchKey) => $"sched-{branchKey}";
    internal static string InstanceKeyFor(string branchKey, string instanceKey) => $"sched-{branchKey}-{instanceKey}";

    public async Task<ScheduleView?> GetBranchScheduleAsync(string branchKey, CancellationToken ct = default)
    {
        if (await branches.GetByKeyAsync(branchKey, ct) is null)
            return null;
        return ToView(await automations.GetByKeyAsync(BranchKeyFor(branchKey), ct));
    }

    public async Task<ScheduleView?> GetInstanceScheduleAsync(string branchKey, string instanceKey, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null || await instances.GetByKeyAsync(branch.Id, instanceKey, ct) is null)
            return null;
        return ToView(await automations.GetByKeyAsync(InstanceKeyFor(branchKey, instanceKey), ct));
    }

    public async Task<ScheduleView?> SetBranchScheduleAsync(string branchKey, bool enabled, string cron, CancellationToken ct = default)
    {
        if (await branches.GetByKeyAsync(branchKey, ct) is null)
            return null;
        return await UpsertOrDeleteAsync(
            BranchKeyFor(branchKey), $"Scheduled comparison: {branchKey}",
            AutomationScopeKind.Branch, branchKey, instanceKey: null, enabled, cron, ct);
    }

    public async Task<ScheduleView?> SetInstanceScheduleAsync(string branchKey, string instanceKey, bool enabled, string cron, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null || await instances.GetByKeyAsync(branch.Id, instanceKey, ct) is null)
            return null;
        return await UpsertOrDeleteAsync(
            InstanceKeyFor(branchKey, instanceKey), $"Scheduled comparison: {branchKey}/{instanceKey}",
            AutomationScopeKind.Instance, branchKey, instanceKey, enabled, cron, ct);
    }

    private async Task<ScheduleView> UpsertOrDeleteAsync(
        string key, string name, AutomationScopeKind scopeKind, string branchKey, string? instanceKey,
        bool enabled, string cron, CancellationToken ct)
    {
        if (!StorageKeyValidator.IsValidKey(key))
            throw new ScheduleValidationException($"Schedule key '{key}' is invalid (branch/instance key too long?).");

        var existing = await automations.GetByKeyAsync(key, ct);

        if (!enabled)
        {
            if (existing is not null)
                await automations.DeleteAsync(existing.Id, ct);
            return new ScheduleView(false, null);
        }

        if (!AutomationCadence.TryComputeNext(cron, null, DateTimeOffset.UtcNow, out var next, out var cronError))
            throw new ScheduleValidationException(cronError!);

        if (existing is null)
        {
            var created = await automations.CreateAsync(new Automation
            {
                Id = Guid.NewGuid(), Key = key, Name = name,
                ScopeKind = scopeKind, BranchKey = branchKey, InstanceKey = instanceKey,
                Cron = cron, Enabled = true, NextRunAt = next,
                Steps = [new AutomationStep { Type = AutomationStepType.ScheduledComparison }],
            }, ct);
            return ToView(created)!;
        }

        var updated = await automations.UpdateAsync(existing with
        {
            Name = name, ScopeKind = scopeKind,
            BranchKey = branchKey, InstanceKey = instanceKey, Cron = cron, Enabled = true,
            IntervalSeconds = null, NextRunAt = existing.Cron == cron && existing.Enabled ? existing.NextRunAt : next,
            Steps = [new AutomationStep { Type = AutomationStepType.ScheduledComparison }],
        }, existing.Version, ct);
        return ToView(updated)!;
    }

    private static ScheduleView? ToView(Automation? automation) =>
        automation is null ? new ScheduleView(false, null) : new ScheduleView(automation.Enabled, automation.Cron);
}

/// <summary>A scheduled-run setting was rejected (e.g. the composed automation key is invalid, or the cron does not parse).</summary>
public sealed class ScheduleValidationException(string message) : Exception(message);
