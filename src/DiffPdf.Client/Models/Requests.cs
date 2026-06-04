namespace DiffPdf.Client;

/// <summary>Branch + instance a job belongs to.</summary>
public sealed record JobScope(string BranchKey, string InstanceKey);

/// <summary>Create a branch (top-level scope, e.g. "Alfa").</summary>
public sealed record CreateBranchRequest(string Key, string Name);

/// <summary>Update a branch's name/enabled (Key is the immutable identity). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateBranchRequest(string Name, bool Enabled, long Version);

/// <summary>
/// Create an instance under a branch. <paramref name="BasePath"/> (local / UNC /
/// <c>share:</c> alias) holds the old/new/reports subfolders.
/// </summary>
public sealed record CreateInstanceRequest(string Key, string Name, string BasePath, string? CredentialProfile = null);

/// <summary>Update an instance's name/basePath/credentialProfile/enabled (Key is the immutable identity). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateInstanceRequest(string Name, string BasePath, string? CredentialProfile, bool Enabled, long Version);

/// <summary>Create a trigger bound to a branch + instance.</summary>
public sealed record CreateTriggerRequest
{
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public TriggerSpec? Spec { get; init; }
}

/// <summary>Partial update of a trigger (null = leave unchanged). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateTriggerRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
    public TriggerSpec? Spec { get; init; }
    public long? Version { get; init; }
}

/// <summary>
/// Upsert a scope's configuration: the source selectors plus this level's custom payloads. <see cref="Version"/>
/// guards concurrent edits (null = overwrite the latest).
/// </summary>
public sealed record UpsertScopeConfigRequest
{
    public ConfigSource TriggerSource { get; init; }
    public TriggerConfig TriggerConfig { get; init; } = new();
    public ConfigSource ComparerSource { get; init; }
    public ComparisonOptions ComparisonOptions { get; init; } = new();
    public long? Version { get; init; }
}

/// <summary>Enable/disable a scope's scheduled comparison. <see cref="Cron"/> is a 5-field UTC cron (required when enabled).</summary>
public sealed record SetScheduleRequest
{
    public bool Enabled { get; init; }
    public string? Cron { get; init; }
}

/// <summary>A run-queue control request (enqueue/run/pause/resume/stop) for an instance or a whole branch.</summary>
public sealed record QueueActionRequest(QueueAction Action);

/// <summary>Compare a single old/new PDF pair synchronously.</summary>
public sealed record SingleComparisonRequest
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public ComparisonOptions Options { get; init; } = new();
}

/// <summary>Create a control check (the unified control/monitoring mechanism).</summary>
public sealed record CreateCheckRequest
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public required CheckType Type { get; init; }
    public CheckScopeKind ScopeKind { get; init; } = CheckScopeKind.Global;
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
    public IReadOnlyList<NotificationEvent>? Events { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Update a control check. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateCheckRequest
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public required CheckType Type { get; init; }
    public required long Version { get; init; }
    public CheckScopeKind ScopeKind { get; init; } = CheckScopeKind.Global;
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
    public IReadOnlyList<NotificationEvent>? Events { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Create a notification subscription routing finished-batch events to a webhook or e-mail.</summary>
public sealed record CreateSubscriptionRequest
{
    public required string Channel { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Update a subscription. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateSubscriptionRequest
{
    public required string Channel { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public bool Enabled { get; init; } = true;
    public required long Version { get; init; }
}
