namespace DiffPdf.Core.Models;

/// <summary>Domain scope a job belongs to: a business instance and a project under it.</summary>
public sealed record JobScope(string BusinessInstanceKey, string ProjectKey);

/// <summary>A business instance (e.g. "Alfa", "RNew", "ROld") — domain data, not infrastructure.</summary>
public sealed record BusinessInstance
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}

/// <summary>A project/customer scope under a business instance.</summary>
public sealed record ComparisonProject
{
    public required Guid Id { get; init; }
    public required Guid BusinessInstanceId { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}

/// <summary>Filter for listing jobs.</summary>
public sealed record JobListQuery
{
    public string? BusinessInstanceKey { get; init; }
    public string? ProjectKey { get; init; }
    public JobStatus? Status { get; init; }
    public int Limit { get; init; } = 100;
}
