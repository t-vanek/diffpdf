namespace DiffPdf.Core.Models;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A unit of asynchronous batch work tracked by the API and persisted as the source of truth.</summary>
public sealed record ComparisonJob
{
    public required Guid Id { get; init; }

    public required BatchComparisonRequest Request { get; init; }

    // --- Domain scope ---
    public required Guid BranchId { get; init; }
    public required Guid InstanceId { get; init; }
    public string BranchKey => Request.Scope.BranchKey;
    public string InstanceKey => Request.Scope.InstanceKey;

    public JobStatus Status { get; init; } = JobStatus.Queued;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Processed file pairs / total discovered pairs.</summary>
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }

    public double Progress => TotalCount == 0 ? 0 : (double)ProcessedCount / TotalCount;

    public BatchComparisonReport? Report { get; init; }

    public string? Error { get; init; }

    // --- Optimistic concurrency + lease (multi-instance worker) ---
    public long Version { get; init; } = 1;
    public string? LockedBy { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
}
