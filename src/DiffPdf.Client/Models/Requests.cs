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
}

/// <summary>Partial update of a trigger (null = leave unchanged). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateTriggerRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
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

/// <summary>One step of an automation's pipeline.</summary>
public sealed record AutomationStepInput
{
    public required AutomationStepType Type { get; init; }
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

/// <summary>Create an automation (triggers + ordered step pipeline + execution policy).</summary>
public sealed record CreateAutomationRequest
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public required IReadOnlyList<AutomationStepInput> Steps { get; init; }
    public AutomationScopeKind ScopeKind { get; init; } = AutomationScopeKind.Global;
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public IReadOnlyList<NotificationEvent>? EventTriggers { get; init; }
    public int EventDebounceSeconds { get; init; } = 60;
    public int TimeoutSeconds { get; init; } = 600;
    public int MaxAttempts { get; init; } = 1;
    public int RetryDelaySeconds { get; init; } = 30;
    public int FailureThreshold { get; init; } = 3;
    public IReadOnlyList<NotificationEvent>? Events { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>False = mute outbound notifications (the automation still runs; runs stay in history).</summary>
    public bool NotificationsEnabled { get; init; } = true;
}

/// <summary>Update an automation. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateAutomationRequest
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public required IReadOnlyList<AutomationStepInput> Steps { get; init; }
    public required long Version { get; init; }
    public AutomationScopeKind ScopeKind { get; init; } = AutomationScopeKind.Global;
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public IReadOnlyList<NotificationEvent>? EventTriggers { get; init; }
    public int EventDebounceSeconds { get; init; } = 60;
    public int TimeoutSeconds { get; init; } = 600;
    public int MaxAttempts { get; init; } = 1;
    public int RetryDelaySeconds { get; init; } = 30;
    public int FailureThreshold { get; init; } = 3;
    public IReadOnlyList<NotificationEvent>? Events { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>False = mute outbound notifications (the automation still runs; runs stay in history).</summary>
    public bool NotificationsEnabled { get; init; } = true;
}

/// <summary>Create an e-mail notification rule (recipients + events + optional branch/instance scope; empty = any).</summary>
public sealed record CreateSubscriptionRequest
{
    public string Name { get; init; } = "";
    public required IReadOnlyList<string> Recipients { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public IReadOnlyList<string> BranchKeys { get; init; } = [];
    public IReadOnlyList<string> InstanceKeys { get; init; } = [];
    public bool Enabled { get; init; } = true;
}

/// <summary>Update an e-mail notification rule. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateSubscriptionRequest
{
    public string Name { get; init; } = "";
    public required IReadOnlyList<string> Recipients { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public IReadOnlyList<string> BranchKeys { get; init; } = [];
    public IReadOnlyList<string> InstanceKeys { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public required long Version { get; init; }
}

/// <summary>Save the SMTP settings. A blank/omitted <see cref="Password"/> keeps the stored one. <see cref="Version"/>
/// guards concurrent edits (pass 0 for the first save).</summary>
public sealed record UpdateEmailSettingsRequest
{
    public required string Host { get; init; }
    public int Port { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string FromAddress { get; init; }
    public string? FromName { get; init; }
    public long Version { get; init; }
}

/// <summary>Send a one-off test e-mail to verify the saved SMTP settings.</summary>
public sealed record SendTestEmailRequest
{
    public required string To { get; init; }
}

/// <summary>Send a job's highlighted diff PDFs by e-mail — one pair, a chosen set, or every differing pair.</summary>
public sealed record SendJobDiffsRequest
{
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>Relative paths (report <c>RelativePath</c>) of the pairs to send; null/empty = all differing pairs.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Optional free-text note placed at the top of the mail body.</summary>
    public string? Note { get; init; }

    /// <summary>Pack the attachments into one ZIP; null = automatic (many or large files).</summary>
    public bool? Zip { get; init; }
}
