namespace DiffPdf.Core.Models;

/// <summary>
/// A recurring batch definition: run a given branch/instance on a cron cadence with its
/// own comparison options and CI gate. Managed at runtime through the API and persisted
/// in the store — not bound from configuration. The same definition also backs the
/// on-demand "run now" action.
/// </summary>
public sealed record ComparisonSchedule
{
    public required Guid Id { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// Denormalized branch/instance keys so the scheduler's per-tick hot path is a single
    /// <c>WHERE enabled</c> read with no join. The launcher re-resolves them by key and
    /// re-checks <c>Enabled</c>, so a stale key self-heals (skipped with a warning).
    /// </summary>
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }

    /// <summary>Key identifying this schedule within its instance (used in REST routes); unique per instance.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }

    /// <summary>Standard 5-field cron expression evaluated in UTC, e.g. <c>0 2 * * *</c> (daily 02:00).</summary>
    public required string Cron { get; init; }

    /// <summary>Comparison tuning applied to every run of this schedule.</summary>
    public ComparisonOptions Options { get; init; } = new();

    /// <summary>Optional CI gate evaluated for every run of this schedule. Null = no gating.</summary>
    public BatchGate? Gate { get; init; }

    /// <summary>Glob-style search pattern relative to each folder.</summary>
    public string SearchPattern { get; init; } = "*.pdf";

    public bool Recursive { get; init; } = true;

    /// <summary>Maximum number of file pairs compared concurrently. 0 = processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When this schedule last launched a batch (set by the scheduler / run-now); null until first run.</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    public long Version { get; init; } = 1;
}
