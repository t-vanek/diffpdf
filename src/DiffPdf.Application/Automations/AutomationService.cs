using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Automations;

/// <summary>Fields for creating/updating an automation (shared; the Version for an update is passed separately).</summary>
public sealed record AutomationInput(
    string Key,
    string Name,
    AutomationScopeKind ScopeKind,
    string? BranchKey,
    string? InstanceKey,
    string? Cron,
    int? IntervalSeconds,
    IReadOnlyList<NotificationEvent>? EventTriggers,
    int EventDebounceSeconds,
    IReadOnlyList<AutomationStep>? Steps,
    int TimeoutSeconds,
    int MaxAttempts,
    int RetryDelaySeconds,
    int FailureThreshold,
    IReadOnlyList<NotificationEvent>? Events,
    bool Enabled);

/// <summary>Invalid automation input (bad key, missing scope keys, bad cadence, no steps, out-of-range
/// policy). Maps to 400 at the endpoint.</summary>
public sealed class AutomationValidationException(string message) : Exception(message);

/// <summary>A run-now request found a run already in flight. Maps to 409 at the endpoint.</summary>
public sealed class AutomationBusyException(string message) : Exception(message);

/// <summary>
/// Automation CRUD plus run-now and run history. Validation, entity assembly and the persisted-schedule
/// (<c>NextRunAt</c>) bookkeeping live here; store conflicts (duplicate key / concurrency) propagate and
/// are mapped to 409 by the endpoint. A missing automation is a null return.
/// </summary>
public interface IAutomationService
{
    Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct = default);
    Task<Automation?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Automation> CreateAsync(AutomationInput input, CancellationToken ct = default);
    Task<Automation?> UpdateAsync(Guid id, AutomationInput input, long version, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Runs the automation now (manual trigger). Throws <see cref="AutomationBusyException"/>
    /// when a run is already in flight; the schedule's <c>NextRunAt</c> is never touched.</summary>
    Task<AutomationRun?> RunNowAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<AutomationRun>?> ListRunsAsync(Guid id, int limit, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AutomationService(
    IAutomationStore store, IAutomationRunner runner, IAutomationRunStore runs) : IAutomationService
{
    public async Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct = default) => await store.ListAsync(ct);

    public async Task<Automation?> GetAsync(Guid id, CancellationToken ct = default) => await store.GetAsync(id, ct);

    public async Task<Automation> CreateAsync(AutomationInput input, CancellationToken ct = default)
    {
        Validate(input);
        var automation = new Automation
        {
            Id = Guid.NewGuid(),
            Key = input.Key,
            Name = string.IsNullOrWhiteSpace(input.Name) ? input.Key : input.Name,
            ScopeKind = input.ScopeKind,
            BranchKey = input.BranchKey,
            InstanceKey = input.InstanceKey,
            Cron = input.Cron,
            IntervalSeconds = input.IntervalSeconds,
            EventTriggers = input.EventTriggers ?? [],
            EventDebounceSeconds = input.EventDebounceSeconds,
            Steps = input.Steps!,
            TimeoutSeconds = input.TimeoutSeconds,
            MaxAttempts = input.MaxAttempts,
            RetryDelaySeconds = input.RetryDelaySeconds,
            FailureThreshold = input.FailureThreshold,
            Events = input.Events ?? [],
            Enabled = input.Enabled,
            // Seed the persisted schedule at creation — never firing immediately; the first due time is
            // the next occurrence. Manual/event-only automations keep null.
            NextRunAt = ComputeNextOrThrow(input.Cron, input.IntervalSeconds),
        };
        return await store.CreateAsync(automation, ct);
    }

    public async Task<Automation?> UpdateAsync(Guid id, AutomationInput input, long version, CancellationToken ct = default)
    {
        Validate(input);
        var existing = await store.GetAsync(id, ct);
        if (existing is null) return null;

        // Recompute the persisted schedule only when it would otherwise be wrong: the cadence changed, or
        // the automation is being re-enabled (a stale due time from the disabled period must not fire a
        // surprise backlog). An unchanged enabled schedule keeps its phase.
        bool cadenceChanged = existing.Cron != input.Cron || existing.IntervalSeconds != input.IntervalSeconds;
        bool reEnabled = !existing.Enabled && input.Enabled;
        var nextRunAt = !AutomationCadence.HasSchedule(input.Cron, input.IntervalSeconds)
            ? null
            : cadenceChanged || reEnabled || existing.NextRunAt is null
                ? ComputeNextOrThrow(input.Cron, input.IntervalSeconds)
                : existing.NextRunAt;

        var updated = existing with
        {
            Key = input.Key,
            Name = string.IsNullOrWhiteSpace(input.Name) ? input.Key : input.Name,
            ScopeKind = input.ScopeKind,
            BranchKey = input.BranchKey,
            InstanceKey = input.InstanceKey,
            Cron = input.Cron,
            IntervalSeconds = input.IntervalSeconds,
            EventTriggers = input.EventTriggers ?? [],
            EventDebounceSeconds = input.EventDebounceSeconds,
            Steps = input.Steps!,
            TimeoutSeconds = input.TimeoutSeconds,
            MaxAttempts = input.MaxAttempts,
            RetryDelaySeconds = input.RetryDelaySeconds,
            FailureThreshold = input.FailureThreshold,
            Events = input.Events ?? [],
            Enabled = input.Enabled,
            NextRunAt = nextRunAt,
        };
        return await store.UpdateAsync(updated, version, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await store.DeleteAsync(id, ct);

    public async Task<AutomationRun?> RunNowAsync(Guid id, CancellationToken ct = default)
    {
        var automation = await store.GetAsync(id, ct);
        if (automation is null) return null;

        // Same claim the engine and the event handler take — a manual run never overlaps a scheduled one.
        if (!await store.TryBeginRunAsync(
                id, DateTimeOffset.UtcNow, advanceNextRunAtTo: null,
                AutomationCadence.StaleClaimWindow(automation), ct))
            throw new AutomationBusyException($"Automation '{automation.Key}' is already running.");

        return await runner.RunAsync(automation, AutomationTriggerKind.Manual, "manual", chainDepth: 0, ct);
    }

    public async Task<IReadOnlyList<AutomationRun>?> ListRunsAsync(Guid id, int limit, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is null) return null;
        return await runs.ListByAutomationAsync(id, limit, ct);
    }

    private static DateTimeOffset? ComputeNextOrThrow(string? cron, int? intervalSeconds)
    {
        if (!AutomationCadence.TryComputeNext(cron, intervalSeconds, DateTimeOffset.UtcNow, out var next, out var error))
            throw new AutomationValidationException(error!);
        return next;
    }

    private static void Validate(AutomationInput input)
    {
        if (!StorageKeyValidator.IsValidKey(input.Key))
            throw new AutomationValidationException("Key must be 1-64 chars of [a-zA-Z0-9_.-] with no '..'.");
        if (input.ScopeKind is AutomationScopeKind.Branch or AutomationScopeKind.Instance && string.IsNullOrWhiteSpace(input.BranchKey))
            throw new AutomationValidationException("BranchKey is required for Branch / Instance scope.");
        if (input.ScopeKind is AutomationScopeKind.Instance && string.IsNullOrWhiteSpace(input.InstanceKey))
            throw new AutomationValidationException("InstanceKey is required for Instance scope.");

        if (input.Steps is not { Count: > 0 })
            throw new AutomationValidationException("At least one step is required.");

        // No trigger at all is legal — a manual-only automation. A set cadence must be valid.
        if (!AutomationCadence.TryComputeNext(input.Cron, input.IntervalSeconds, DateTimeOffset.UtcNow, out _, out var cadenceError))
            throw new AutomationValidationException(cadenceError!);

        if (input.EventDebounceSeconds < 0)
            throw new AutomationValidationException("EventDebounceSeconds must be >= 0.");
        if (input.TimeoutSeconds is < 1 or > 86_400)
            throw new AutomationValidationException("TimeoutSeconds must be between 1 and 86400.");
        if (input.MaxAttempts is < 1 or > 10)
            throw new AutomationValidationException("MaxAttempts must be between 1 and 10.");
        if (input.RetryDelaySeconds is < 0 or > 3_600)
            throw new AutomationValidationException("RetryDelaySeconds must be between 0 and 3600.");
        if (input.FailureThreshold < 1)
            throw new AutomationValidationException("FailureThreshold must be >= 1.");
    }
}
