namespace DiffPdf.Persistence.SqlServer.Entities;

public sealed class BranchEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InstanceEntity
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string? CredentialProfile { get; set; }
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
    public Guid BranchId { get; set; }
    public Guid InstanceId { get; set; }
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

public sealed class ScheduleEntity
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid InstanceId { get; set; }
    public string BranchKey { get; set; } = string.Empty;
    public string InstanceKey { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Cron { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = string.Empty;
    public string? GateJson { get; set; }
    public string SearchPattern { get; set; } = "*.pdf";
    public bool Recursive { get; set; } = true;
    public int MaxDegreeOfParallelism { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class SubscriptionEntity
{
    public Guid Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string EventsJson { get; set; } = string.Empty;
    public string? BranchKey { get; set; }
    public string? InstanceKey { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}
