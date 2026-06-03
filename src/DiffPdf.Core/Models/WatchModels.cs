namespace DiffPdf.Core.Models;

/// <summary>
/// A folder-watch on an instance's <c>new/</c> folder: launches a batch once a drop of PDFs has
/// settled. Managed at runtime through the API and persisted — one watch per instance. (The
/// poll cadence is a process-level constant; only the per-instance watch is runtime-managed.)
/// </summary>
public sealed record FolderWatch
{
    public required Guid Id { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid InstanceId { get; init; }

    /// <summary>Denormalized keys so the watcher's per-tick hot path needs no join.</summary>
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }

    /// <summary>How long the <c>new/</c> folder must be unchanged before a batch launches (lets a drop finish writing).</summary>
    public int StabilitySeconds { get; init; } = 30;

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When this watch last launched a batch (set after a stable drop fires); null until first trigger.</summary>
    public DateTimeOffset? LastTriggeredAt { get; init; }

    public long Version { get; init; } = 1;

    public TimeSpan Stability => TimeSpan.FromSeconds(StabilitySeconds);
}
