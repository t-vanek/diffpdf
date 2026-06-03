namespace DiffPdf.Core.Models;

/// <summary>Outcome of one schedule run (the batch it launched).</summary>
public enum ScheduleRunOutcome
{
    /// <summary>Launched; the batch has not finished yet.</summary>
    Pending,
    /// <summary>Batch completed and (if gated) passed.</summary>
    Passed,
    /// <summary>Batch completed but violated its CI gate.</summary>
    GateViolated,
    /// <summary>Batch hard-failed (e.g. indexing error).</summary>
    Failed,
}

/// <summary>
/// One launch of a schedule: links the schedule to the job it started and records the outcome
/// once the batch finishes. Recorded by the scheduler / run-now at launch and patched by job id
/// when the batch completes or fails. Survives job retention — <see cref="JobId"/> is not a
/// foreign key, so run history outlives the pruned job.
/// </summary>
public sealed record ScheduleRun
{
    public required Guid Id { get; init; }
    public required Guid ScheduleId { get; init; }
    public required Guid JobId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public ScheduleRunOutcome Outcome { get; init; } = ScheduleRunOutcome.Pending;
    public int Differing { get; init; }
    public int Errors { get; init; }
    public int FilesWithContentErrors { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<string> GateViolations { get; init; } = [];
    public string? Error { get; init; }
}
