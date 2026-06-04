using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Triggers;

/// <summary>Fields for creating a trigger (the instance is identified by its branch + instance keys).</summary>
public sealed record CreateTriggerInput
{
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public TriggerActionType ActionType { get; init; } = TriggerActionType.RunComparison;
    public bool Enabled { get; init; } = true;
    public TriggerSpec Spec { get; init; } = new();
}

/// <summary>Partial update of a trigger (null = leave unchanged).</summary>
public sealed record UpdateTriggerInput
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
    public TriggerSpec? Spec { get; init; }
    public long? ExpectedVersion { get; init; }
}

/// <summary>Outcome of running a trigger: success enqueues a batch job and returns its id.</summary>
public sealed record TriggerRunResult(bool Success, Guid TriggerId, Guid? BatchJobId, string Status, string Message, string? ErrorCode = null);

/// <summary>Validation failures surfaced as a clean error code + message (no internal details leak).</summary>
public sealed class TriggerValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Manages triggers and runs them. Running a trigger validates it (and its scope), then creates and
/// <b>enqueues</b> a batch comparison via <see cref="IBatchLauncher"/> — never synchronously. Every
/// lifecycle action and run is audited. Soft delete preserves run history, jobs and results.
/// </summary>
public interface ITriggerService
{
    Task<Trigger> CreateAsync(CreateTriggerInput input, string? actor, JobSource source, CancellationToken ct = default);
    Task<Trigger?> UpdateAsync(Guid id, UpdateTriggerInput patch, string? actor, CancellationToken ct = default);
    Task<Trigger?> SetEnabledAsync(Guid id, bool enabled, string? actor, JobSource source, CancellationToken ct = default);
    Task<Trigger?> DeleteAsync(Guid id, string? actor, JobSource source, CancellationToken ct = default);
    Task<TriggerRunResult> RunAsync(Guid id, JobSource source, string? actor, string? idempotencyKey, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TriggerService(
    ITriggerStore triggers,
    ITriggerRunStore runs,
    IAuditLogStore audit,
    IBatchLauncher launcher,
    IBranchStore branches,
    IInstanceStore instances,
    ITriggerEventPublisher events,
    ILogger<TriggerService> logger) : ITriggerService
{
    public async Task<Trigger> CreateAsync(CreateTriggerInput input, string? actor, JobSource source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new TriggerValidationException("INVALID_NAME", "Název spouštěče je povinný.");
        if (input.ActionType != TriggerActionType.RunComparison)
            throw new TriggerValidationException("UNSUPPORTED_TYPE", "Tento typ spouštěče není podporován.");

        var branch = await branches.GetByKeyAsync(input.BranchKey, ct)
            ?? throw new TriggerValidationException("BRANCH_NOT_FOUND", "Větev neexistuje.");
        var instance = await instances.GetByKeyAsync(branch.Id, input.InstanceKey, ct)
            ?? throw new TriggerValidationException("INSTANCE_NOT_FOUND", "Instance neexistuje.");

        var created = await triggers.CreateAsync(new Trigger
        {
            Id = Guid.NewGuid(),
            BranchId = branch.Id,
            InstanceId = instance.Id,
            BranchKey = branch.Key,
            InstanceKey = instance.Key,
            Name = input.Name.Trim(),
            Description = input.Description,
            ActionType = input.ActionType,
            Status = input.Enabled ? TriggerStatus.Active : TriggerStatus.Inactive,
            Enabled = input.Enabled,
            IsDefault = false,
            Spec = input.Spec,
            CreatedBy = actor,
        }, ct);

        await AuditAsync("trigger.created", created.Id, source, actor, $"{created.BranchKey}/{created.InstanceKey} '{created.Name}'", ct);
        await PublishAsync("trigger.created", created, source, ct: ct);
        return created;
    }

    public async Task<Trigger?> UpdateAsync(Guid id, UpdateTriggerInput patch, string? actor, CancellationToken ct = default)
    {
        var existing = await triggers.GetAsync(id, ct);
        if (existing is null || existing.IsDeleted) return null;

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(patch.Name) ? existing.Name : patch.Name.Trim(),
            Description = patch.Description ?? existing.Description,
            Enabled = patch.Enabled ?? existing.Enabled,
            Status = (patch.Enabled ?? existing.Enabled) ? TriggerStatus.Active : TriggerStatus.Inactive,
            Spec = patch.Spec ?? existing.Spec,
            UpdatedBy = actor,
        };
        var saved = await triggers.UpdateAsync(updated, patch.ExpectedVersion ?? existing.Version, ct);
        await AuditAsync("trigger.updated", id, JobSource.Manager, actor, null, ct);
        await PublishAsync("trigger.updated", saved, JobSource.Manager, ct: ct);
        return saved;
    }

    public async Task<Trigger?> SetEnabledAsync(Guid id, bool enabled, string? actor, JobSource source, CancellationToken ct = default)
    {
        var result = await triggers.SetEnabledAsync(id, enabled, actor, ct);
        if (result is not null)
        {
            await AuditAsync(enabled ? "trigger.enabled" : "trigger.disabled", id, source, actor, null, ct);
            await PublishAsync(enabled ? "trigger.enabled" : "trigger.disabled", result, source, ct: ct);
        }
        return result;
    }

    public async Task<Trigger?> DeleteAsync(Guid id, string? actor, JobSource source, CancellationToken ct = default)
    {
        var result = await triggers.SoftDeleteAsync(id, actor, ct);
        if (result is not null)
        {
            await AuditAsync("trigger.deleted", id, source, actor, null, ct);
            await PublishAsync("trigger.deleted", result, source, ct: ct);
        }
        return result;
    }

    public async Task<TriggerRunResult> RunAsync(Guid id, JobSource source, string? actor, string? idempotencyKey, CancellationToken ct = default)
    {
        var t = await triggers.GetAsync(id, ct);
        if (t is null)
            return new TriggerRunResult(false, id, null, "failed", "Spouštěč nebyl nalezen.", "TRIGGER_NOT_FOUND");
        if (t.IsDeleted)
            return new TriggerRunResult(false, id, null, "failed", "Spouštěč je smazaný a nelze jej spustit.", "TRIGGER_DELETED");
        if (!t.Enabled)
            return new TriggerRunResult(false, id, null, "failed", "Spouštěč není aktivní a nelze jej spustit.", "TRIGGER_DISABLED");

        // Idempotency: a repeat with the same key returns the originally created batch job (no duplicate).
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var prior = await runs.GetByIdempotencyKeyAsync(id, idempotencyKey!, ct);
            if (prior is { BatchJobId: { } priorJob })
                return new TriggerRunResult(true, id, priorJob, prior.Status, "Porovnání již bylo zařazeno do fronty.");
        }

        LaunchResult launch;
        try
        {
            launch = await launcher.LaunchAsync(t.BranchKey, t.InstanceKey, new LaunchSpec(
                t.Spec.Options, t.Spec.Gate, t.Spec.SearchPattern, t.Spec.Recursive, t.Spec.MaxDegreeOfParallelism, t.Id, source), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Trigger {TriggerId} launch failed.", id);
            return await FailAsync(t, source, actor, "LAUNCH_ERROR", "Spuštění porovnání selhalo.", ex.Message, ct);
        }

        if (!launch.Launched)
        {
            var (code, msg) = launch.Outcome switch
            {
                LaunchOutcome.ScopeNotFound => ("SCOPE_NOT_FOUND", "Větev nebo instance neexistuje nebo je zakázaná."),
                LaunchOutcome.Unreachable => ("INSTANCE_UNREACHABLE", "Základní cesta instance není dostupná."),
                LaunchOutcome.NothingToCompare => ("NOTHING_TO_COMPARE", "Není co porovnávat."),
                _ => ("LAUNCH_FAILED", "Porovnání se nepodařilo spustit."),
            };
            return await FailAsync(t, source, actor, code, msg, launch.Detail, ct);
        }

        var run = new TriggerRun
        {
            Id = Guid.NewGuid(),
            TriggerId = t.Id,
            BatchJobId = launch.JobId,
            Source = source,
            Status = "queued",
            RequestedBy = actor,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
        };
        try
        {
            await runs.AddAsync(run, ct);
        }
        catch (DuplicateKeyException)
        {
            // Concurrent request with the same idempotency key won the race — return its job.
            var prior = idempotencyKey is null ? null : await runs.GetByIdempotencyKeyAsync(t.Id, idempotencyKey, ct);
            if (prior is { BatchJobId: { } priorJob })
                return new TriggerRunResult(true, t.Id, priorJob, prior.Status, "Porovnání již bylo zařazeno do fronty.");
        }
        await triggers.TouchLastRunAsync(t.Id, run.StartedAt, "Launched", ct);
        await AuditAsync("trigger.run.requested", t.Id, source, actor, $"batchJobId={launch.JobId}", ct);
        // Real-time: the batch job exists + is queued (published after the outbox commit).
        await PublishAsync("trigger.run.requested", t, source, launch.JobId, "queued", ct: ct);
        await PublishAsync("batch.created", t, source, launch.JobId, "created", ct: ct);
        await PublishAsync("batch.queued", t, source, launch.JobId, "queued", ct: ct);
        return new TriggerRunResult(true, t.Id, launch.JobId, "queued", "Porovnání bylo zařazeno do fronty.");
    }

    private async Task<TriggerRunResult> FailAsync(Trigger t, JobSource source, string? actor, string errorCode, string message, string? detail, CancellationToken ct)
    {
        // A failed run produced no batch job, so it never claims the idempotency key (a later retry may proceed).
        await runs.AddAsync(new TriggerRun
        {
            Id = Guid.NewGuid(), TriggerId = t.Id, Source = source, Status = "failed",
            FinishedAt = DateTimeOffset.UtcNow, Error = detail ?? message, RequestedBy = actor,
        }, ct);
        await triggers.TouchLastRunAsync(t.Id, DateTimeOffset.UtcNow, "Failed", ct);
        await AuditAsync("trigger.run.failed", t.Id, source, actor, $"{errorCode}: {message}", ct);
        return new TriggerRunResult(false, t.Id, null, "failed", message, errorCode);
    }

    private Task PublishAsync(string eventType, Trigger t, JobSource source, Guid? batchJobId = null, string? status = null, CancellationToken ct = default) =>
        events.PublishAsync(new TriggerEvent(
            eventType, t.Id, batchJobId, t.BranchId, t.InstanceId, t.BranchKey, t.InstanceKey,
            Status: status, Source: source.ToString()), ct);

    private Task AuditAsync(string action, Guid triggerId, JobSource source, string? actor, string? detail, CancellationToken ct) =>
        audit.AddAsync(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Source = source.ToString(),
            Action = action,
            EntityType = "Trigger",
            EntityId = triggerId.ToString(),
            Detail = detail,
        }, ct);
}
