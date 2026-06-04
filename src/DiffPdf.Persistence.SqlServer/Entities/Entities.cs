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
    public DateTimeOffset? ArtifactsPrunedAt { get; set; }
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

public sealed class ControlCheckEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ScopeKind { get; set; } = "Global";
    public string? BranchKey { get; set; }
    public string? InstanceKey { get; set; }
    public string? Cron { get; set; }
    public int? IntervalSeconds { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public string EventsJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastOutcome { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ControlCheckRunEntity
{
    public Guid Id { get; set; }
    public Guid CheckId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Outcome { get; set; } = "Ok";
    public string? Detail { get; set; }
}
