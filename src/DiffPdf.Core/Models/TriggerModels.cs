namespace DiffPdf.Core.Models;

/// <summary>The kind of action a <see cref="Trigger"/> performs. Today only batch comparison; extensible.</summary>
public enum TriggerActionType
{
    /// <summary>Launch a batch comparison for the trigger's instance.</summary>
    RunComparison,
}

/// <summary>Lifecycle state of a <see cref="Trigger"/>.</summary>
public enum TriggerStatus
{
    Active,
    Inactive,
    Disabled,
    Running,
    Pending,
    Error,
    Deleted,
}

/// <summary>Where a run / batch job was launched from (recorded for audit and the job's provenance).</summary>
public enum JobSource
{
    /// <summary>Launched from the DiffPDF Remote Manager desktop client.</summary>
    Manager,
    /// <summary>Launched via the REST API by a third-party client.</summary>
    RestApi,
    /// <summary>Launched automatically by the system (e.g. provisioning/backfill).</summary>
    System,
    /// <summary>Launched by a scheduled run.</summary>
    Scheduler,
}

/// <summary>The comparison knobs a trigger runs a batch with (Core mirror of the messaging LaunchSpec).</summary>
public sealed record TriggerSpec
{
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public ComparisonOptions Options { get; init; } = new();
    public BatchGate? Gate { get; init; }
    public int MaxDegreeOfParallelism { get; init; }
}

/// <summary>
/// A managed launch entity bound to an instance (and its branch). Running it creates a batch comparison
/// (<see cref="ComparisonJob"/>) and enqueues it — never synchronously. Soft-deleted (never hard-deleted)
/// so run history, jobs and results survive. The <see cref="Id"/> is the REST/API identifier.
/// </summary>
public sealed record Trigger
{
    public required Guid Id { get; init; }

    public required Guid BranchId { get; init; }
    public required Guid InstanceId { get; init; }
    /// <summary>Branch key (immutable identity) — kept for launching and display without an extra lookup.</summary>
    public required string BranchKey { get; init; }
    /// <summary>Instance key (immutable identity).</summary>
    public required string InstanceKey { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }

    public TriggerActionType ActionType { get; init; } = TriggerActionType.RunComparison;
    public TriggerStatus Status { get; init; } = TriggerStatus.Active;

    /// <summary>Whether the trigger may be run. A disabled trigger keeps its history but cannot launch.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>True for the single auto-provisioned default trigger of an instance.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Comparison parameters used when the trigger launches a batch.</summary>
    public TriggerSpec Spec { get; init; } = new();

    public int RunCount { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    /// <summary>Outcome of the most recent run (e.g. <c>Launched</c>, <c>Failed</c>); null until first run.</summary>
    public string? LastOutcome { get; init; }

    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Soft delete: a deleted trigger cannot run but its history/jobs/results are preserved.</summary>
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    public long Version { get; init; } = 1;
}

/// <summary>
/// One run of a <see cref="Trigger"/>: the batch job it created and the launch outcome. History is never
/// removed when a trigger is edited or soft-deleted, so runs stay auditable.
/// </summary>
public sealed record TriggerRun
{
    public required Guid Id { get; init; }
    public required Guid TriggerId { get; init; }

    /// <summary>The batch job created by this run; null when the run failed before a job was created.</summary>
    public Guid? BatchJobId { get; init; }

    public JobSource Source { get; init; } = JobSource.RestApi;

    /// <summary>Launch status: <c>queued</c> (job enqueued) or <c>failed</c> (validation/launch error).</summary>
    public string Status { get; init; } = "queued";
    public string? Result { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public long? DurationMs { get; init; }
    public string? Error { get; init; }

    public string? RequestedBy { get; init; }

    /// <summary>Idempotency-Key from the run request; a repeat with the same key returns this run's job.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>An append-only audit record for trigger/job lifecycle actions.</summary>
public sealed record AuditEntry
{
    public required Guid Id { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who performed the action (client id / "system"); not a foreign key.</summary>
    public string? Actor { get; init; }

    /// <summary>Origin of the action.</summary>
    public string Source { get; init; } = nameof(JobSource.System);

    /// <summary>Dotted action name, e.g. <c>trigger.created</c>, <c>trigger.run.requested</c>.</summary>
    public required string Action { get; init; }

    /// <summary>The kind of entity acted on, e.g. <c>Trigger</c>.</summary>
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }

    /// <summary>Optional human-readable detail or JSON payload.</summary>
    public string? Detail { get; init; }
}
