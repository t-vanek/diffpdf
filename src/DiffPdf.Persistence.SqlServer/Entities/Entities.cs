namespace DiffPdf.Persistence.SqlServer.Entities;

public sealed class BusinessInstanceEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ProjectEntity
{
    public Guid Id { get; set; }
    public Guid BusinessInstanceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class FilePairTaskEntity
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? OldFilePath { get; set; }
    public string? NewFilePath { get; set; }
    public string Status { get; set; } = "Queued";
    public int AttemptCount { get; set; }
    public string? Error { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; } = 1;
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class JobEntity
{
    public Guid Id { get; set; }
    public Guid BusinessInstanceId { get; set; }
    public Guid ProjectId { get; set; }
    public string Status { get; set; } = "Queued";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string? ReportJson { get; set; }
    public string? Error { get; set; }
    public long Version { get; set; } = 1;
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
