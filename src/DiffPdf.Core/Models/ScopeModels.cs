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

/// <summary>Filter for listing jobs.</summary>
public sealed record JobListQuery
{
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public JobStatus? Status { get; init; }
    public int Limit { get; init; } = 100;
}
