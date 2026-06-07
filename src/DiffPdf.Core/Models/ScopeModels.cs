namespace DiffPdf.Core.Models;

/// <summary>Domain scope a job belongs to: a branch and an instance under it.</summary>
public sealed record JobScope(string BranchKey, string InstanceKey);

/// <summary>A branch (e.g. "Alfa", "RNew", "ROld") — the top-level scope. Domain data, not infrastructure.</summary>
public sealed record Branch
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>When true, the branch's sequential job queue is held: no new instance is released until resumed.</summary>
    public bool QueuePaused { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}

/// <summary>
/// An instance (e.g. "LamaEnergy", "Pragoplyn") under a branch. Binds a customer to a
/// base folder whose <c>old/</c>, <c>new/</c> and <c>reports/</c> subfolders hold the
/// PDFs to compare and the produced outputs.
/// </summary>
public sealed record ComparisonInstance
{
    public required Guid Id { get; init; }
    public required Guid BranchId { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Base folder for this instance: a local path, a UNC path (<c>\\server\share</c>)
    /// or a configured <c>share:</c> alias. Inputs are read from <c>{BasePath}/old</c>
    /// and <c>{BasePath}/new</c>; outputs are written under <c>{BasePath}/reports</c>.
    /// </summary>
    public required string BasePath { get; init; }

    /// <summary>Optional name of a configured credential profile used to reach <see cref="BasePath"/>.</summary>
    public string? CredentialProfile { get; init; }

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}

/// <summary>
/// A lightweight job row for the list view: scalar columns + the denormalized verdict counts, with the branch/
/// instance keys joined in — so the list query never deserializes the (potentially large) request/report JSON.
/// </summary>
public sealed record JobListItem
{
    public Guid Id { get; init; }
    public string BranchKey { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public JobStatus Status { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }

    /// <summary>Verdict, denormalized from the report on completion (null while running / for pre-backfill rows).</summary>
    public int? Differing { get; init; }
    public int? Errors { get; init; }
    public bool? GatePassed { get; init; }

    public double Progress => TotalCount == 0 ? 0 : (double)ProcessedCount / TotalCount;
}

/// <summary>Filter and paging window for listing jobs (newest first).</summary>
public sealed record JobListQuery
{
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public JobStatus? Status { get; init; }

    /// <summary>Filter to batches launched by a specific trigger.</summary>
    public Guid? TriggerId { get; init; }

    /// <summary>Maximum rows to return (page size).</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Rows to skip before the page (offset paging). 0 = first page.</summary>
    public int Offset { get; init; }
}
