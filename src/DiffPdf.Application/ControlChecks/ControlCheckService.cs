using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Application.ControlChecks;

/// <summary>Fields for creating/updating a control check (shared; the Version for an update is passed separately).</summary>
public sealed record CheckInput(
    string Key,
    string Name,
    CheckType Type,
    CheckScopeKind ScopeKind,
    string? BranchKey,
    string? InstanceKey,
    string? Cron,
    int? IntervalSeconds,
    IReadOnlyDictionary<string, string>? Parameters,
    IReadOnlyList<NotificationEvent>? Events,
    bool Enabled);

/// <summary>Invalid control-check input (bad key, missing scope keys, no schedule). Maps to 400 at the endpoint.</summary>
public sealed class CheckValidationException(string message) : Exception(message);

/// <summary>
/// Control-check CRUD plus run-now and run history. Validation and entity assembly live here; store conflicts
/// (duplicate key / concurrency) propagate and are mapped to 409 by the endpoint. A missing check is a null return.
/// </summary>
public interface IControlCheckService
{
    Task<IReadOnlyList<ControlCheck>> ListAsync(CancellationToken ct = default);
    Task<ControlCheck?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ControlCheck> CreateAsync(CheckInput input, CancellationToken ct = default);
    Task<ControlCheck?> UpdateAsync(Guid id, CheckInput input, long version, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ControlCheckRun?> RunAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ControlCheckRun>?> ListRunsAsync(Guid id, int limit, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ControlCheckService(
    IControlCheckStore store, IControlCheckRunner runner, IControlCheckRunStore runs) : IControlCheckService
{
    public async Task<IReadOnlyList<ControlCheck>> ListAsync(CancellationToken ct = default) => await store.ListAsync(ct);

    public async Task<ControlCheck?> GetAsync(Guid id, CancellationToken ct = default) => await store.GetAsync(id, ct);

    public async Task<ControlCheck> CreateAsync(CheckInput input, CancellationToken ct = default)
    {
        Validate(input);
        var check = new ControlCheck
        {
            Id = Guid.NewGuid(),
            Key = input.Key,
            Name = string.IsNullOrWhiteSpace(input.Name) ? input.Key : input.Name,
            Type = input.Type,
            ScopeKind = input.ScopeKind,
            BranchKey = input.BranchKey,
            InstanceKey = input.InstanceKey,
            Cron = input.Cron,
            IntervalSeconds = input.IntervalSeconds,
            Parameters = input.Parameters ?? new Dictionary<string, string>(),
            Events = input.Events ?? [],
            Enabled = input.Enabled,
        };
        return await store.CreateAsync(check, ct);
    }

    public async Task<ControlCheck?> UpdateAsync(Guid id, CheckInput input, long version, CancellationToken ct = default)
    {
        Validate(input);
        var existing = await store.GetAsync(id, ct);
        if (existing is null) return null;

        var updated = existing with
        {
            Key = input.Key,
            Name = string.IsNullOrWhiteSpace(input.Name) ? input.Key : input.Name,
            Type = input.Type,
            ScopeKind = input.ScopeKind,
            BranchKey = input.BranchKey,
            InstanceKey = input.InstanceKey,
            Cron = input.Cron,
            IntervalSeconds = input.IntervalSeconds,
            Parameters = input.Parameters ?? new Dictionary<string, string>(),
            Events = input.Events ?? [],
            Enabled = input.Enabled,
        };
        return await store.UpdateAsync(updated, version, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await store.DeleteAsync(id, ct);

    public async Task<ControlCheckRun?> RunAsync(Guid id, CancellationToken ct = default)
    {
        var check = await store.GetAsync(id, ct);
        if (check is null) return null;
        return await runner.RunAsync(check, ct);
    }

    public async Task<IReadOnlyList<ControlCheckRun>?> ListRunsAsync(Guid id, int limit, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is null) return null;
        return await runs.ListByCheckAsync(id, limit, ct);
    }

    private static void Validate(CheckInput input)
    {
        if (!StorageKeyValidator.IsValidKey(input.Key))
            throw new CheckValidationException("Key must be 1-64 chars of [a-zA-Z0-9_.-] with no '..'.");
        if (input.ScopeKind is CheckScopeKind.Branch or CheckScopeKind.Instance && string.IsNullOrWhiteSpace(input.BranchKey))
            throw new CheckValidationException("BranchKey is required for Branch / Instance scope.");
        if (input.ScopeKind is CheckScopeKind.Instance && string.IsNullOrWhiteSpace(input.InstanceKey))
            throw new CheckValidationException("InstanceKey is required for Instance scope.");
        if (string.IsNullOrWhiteSpace(input.Cron) && !(input.IntervalSeconds is > 0))
            throw new CheckValidationException("Either a cron expression or a positive intervalSeconds is required.");
    }
}
