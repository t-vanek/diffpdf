namespace DiffPdf.Persistence.SqlServer.Entities;

public sealed class BranchEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool QueuePaused { get; set; }
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
    public Guid? TriggerId { get; set; }
    public string Source { get; set; } = "System";
    public int Priority { get; set; }
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

public sealed class TriggerEntity
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid InstanceId { get; set; }
    public string BranchKey { get; set; } = string.Empty;
    public string InstanceKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ActionType { get; set; } = "RunComparison";
    public string Status { get; set; } = "Active";
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int RunCount { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastOutcome { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class TriggerRunEntity
{
    public Guid Id { get; set; }
    public Guid TriggerId { get; set; }
    public Guid? BatchJobId { get; set; }
    public string Source { get; set; } = "RestApi";
    public string Status { get; set; } = "queued";
    public string? Result { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public string? RequestedBy { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class AuditLogEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? Actor { get; set; }
    public string Source { get; set; } = "System";
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
}

public sealed class ScopeConfigurationEntity
{
    public Guid Id { get; set; }
    public string Level { get; set; } = "Global";
    public Guid? BranchId { get; set; }
    public Guid? InstanceId { get; set; }
    public string TriggerSource { get; set; } = "Global";
    public string TriggerConfigJson { get; set; } = "{}";
    public string ComparerSource { get; set; } = "Global";
    public string ComparisonOptionsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}
