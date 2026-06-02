namespace DiffPdf.Core.Models;

public enum FilePairTaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Skipped,
}

/// <summary>
/// One file pair within a batch job, processed and retried independently
/// (phase 5). The aggregated <see cref="Result"/> feeds the final batch report.
/// </summary>
public sealed record FilePairTask
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string RelativePath { get; init; }

    public string? OldFilePath { get; init; }
    public string? NewFilePath { get; init; }

    public FilePairTaskStatus Status { get; init; } = FilePairTaskStatus.Queued;
    public int AttemptCount { get; init; }
    public string? Error { get; init; }

    /// <summary>Per-file outcome once compared.</summary>
    public FilePairResult? Result { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public long Version { get; init; } = 1;
    public string? LockedBy { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
}
