using System.Text.Json.Serialization;

namespace DiffPdf.Client;

/// <summary>A branch (top-level scope).</summary>
public sealed record Branch
{
    public Guid Id { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>A branch with its instance count and live run-queue state (from <c>GET /branches/summary</c>) — one call for the list view.</summary>
public sealed record BranchSummary(Branch Branch, int InstanceCount, BranchQueueState Queue);

/// <summary>An instance under a branch (binds a customer to a base folder).</summary>
public sealed record Instance
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string BasePath { get; init; } = "";
    public string? CredentialProfile { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>Response from creating an instance: the record plus the folder-provisioning result.</summary>
public sealed record CreatedInstanceResponse
{
    public required Instance Instance { get; init; }
    public InstanceStructureReport? Structure { get; init; }
}

// ---------------- Per-scope statistics (branch / instance detail) ----------------

/// <summary>Job counts by status for one scope.</summary>
public sealed record JobStats
{
    public int Queued { get; init; }
    public int Running { get; init; }
    public int Paused { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
}

/// <summary>Control-check counts for one scope.</summary>
public sealed record CheckStats
{
    public int Active { get; init; }
    public int Running { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
}

/// <summary>Automation counts for one scope (umbrella over every automation type; control checks are the only type today).</summary>
public sealed record AutomationStats
{
    public int Active { get; init; }
    public int Running { get; init; }
    public int Paused { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
}

/// <summary>Comparison (file-pair) counts by status for one scope.</summary>
public sealed record ComparisonStats
{
    public int Waiting { get; init; }
    public int Running { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

/// <summary>Aggregated statistics for a single branch or instance scope.</summary>
public sealed record ScopeStatsResponse
{
    public JobStats Jobs { get; init; } = new();
    public CheckStats Checks { get; init; } = new();
    public AutomationStats Automations { get; init; } = new();
    public ComparisonStats Comparisons { get; init; } = new();
}

/// <summary>The configured structure root under which branches/instances are created server-side (from <c>GET /scope/root</c>).</summary>
public sealed record ScopeRootInfo
{
    public string? Root { get; init; }
    public bool Configured { get; init; }
}

// ---------------- Triggers (Spouštěče) ----------------

/// <summary>
/// A scope's stored configuration together with the server-resolved effective configuration. The UI binds
/// the stored sources/payloads for editing and shows the effective values + the level each resolved from
/// (<see cref="TriggerResolvedFrom"/> / <see cref="ComparerResolvedFrom"/>), performing no inheritance itself.
/// </summary>
public sealed record ScopeConfigResponse
{
    public ConfigScopeLevel Level { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }

    // Stored at this level:
    public ConfigSource TriggerSource { get; init; }
    public TriggerConfig TriggerConfig { get; init; } = new();
    public ConfigSource ComparerSource { get; init; }
    public ComparisonOptions ComparisonOptions { get; init; } = new();

    // Server-resolved effective config:
    public TriggerConfig EffectiveTriggerConfig { get; init; } = new();
    public ConfigScopeLevel TriggerResolvedFrom { get; init; }
    public ComparisonOptions EffectiveComparisonOptions { get; init; } = new();
    public ConfigScopeLevel ComparerResolvedFrom { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>A scope's current scheduled-run setting (gear "Konfigurace" → Plán běhu).</summary>
public sealed record ScheduleResponse
{
    public bool Enabled { get; init; }
    public string? Cron { get; init; }
}

/// <summary>One instance's state within its branch's sequential run-queue.</summary>
public sealed record InstanceQueueState
{
    public string BranchKey { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public InstanceQueueStatus Status { get; init; }
    public Guid? ActiveJobId { get; init; }
    public int Priority { get; init; }
}

/// <summary>A branch's run-queue snapshot (hold flag + counts + per-instance states). Also the SignalR "queueState" payload.</summary>
public sealed record BranchQueueState
{
    public string BranchKey { get; init; } = "";
    public bool QueuePaused { get; init; }
    public int Pending { get; init; }
    public int Queued { get; init; }
    public int Running { get; init; }
    public int Paused { get; init; }
    public IReadOnlyList<InstanceQueueState> Instances { get; init; } = [];
}

/// <summary>Result of a queue action: the updated branch state + an optional note to show the user.</summary>
public sealed record QueueActionOutcome
{
    public BranchQueueState State { get; init; } = new();
    public string? Message { get; init; }
}

/// <summary>A trigger as returned by the API.</summary>
public sealed record TriggerResponse
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid InstanceId { get; init; }
    public string BranchKey { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public TriggerActionType ActionType { get; init; }
    public TriggerStatus Status { get; init; }
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public int RunCount { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastOutcome { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>One trigger run as returned by the API.</summary>
public sealed record TriggerRunResponse
{
    public Guid Id { get; init; }
    public Guid TriggerId { get; init; }
    public Guid? BatchJobId { get; init; }
    public JobSource Source { get; init; }
    public string Status { get; init; } = "";
    public string? Result { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long? DurationMs { get; init; }
    public string? Error { get; init; }
    public string? RequestedBy { get; init; }
}

/// <summary>Result of running a trigger (the run does not wait for the comparison to finish).</summary>
public sealed record RunTriggerResponse
{
    public bool Success { get; init; }
    public Guid TriggerId { get; init; }
    public Guid? BatchJobId { get; init; }
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string? ErrorCode { get; init; }
}

/// <summary>One audit entry as returned by the API.</summary>
public sealed record AuditEntryResponse
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    public string? Actor { get; init; }
    public string Source { get; init; } = "";
    public string Action { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string? EntityId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>A real-time trigger/batch/comparison event pushed over SignalR (<c>"triggerEvent"</c>).</summary>
public sealed record TriggerEvent
{
    public string EventType { get; init; } = "";
    public Guid TriggerId { get; init; }
    public Guid? BatchJobId { get; init; }
    public Guid? BranchId { get; init; }
    public Guid? InstanceId { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public string? Status { get; init; }
    public string? Result { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long? DurationMs { get; init; }
    public string? Source { get; init; }
    public string? Message { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ResultReference { get; init; }
}

/// <summary>State of one required subfolder after inspecting / ensuring it.</summary>
public sealed record StructureItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public StructureItemState State { get; init; }
    public string? Detail { get; init; }
    /// <summary>Number of *.pdf (recursive) in old/new; null for reports or a missing folder.</summary>
    public int? PdfCount { get; init; }
    /// <summary>Complete relative paths; present only when files were requested.</summary>
    public IReadOnlyList<string>? Files { get; init; }
}

/// <summary>Result of inspecting / ensuring an instance's old/new/reports skeleton.</summary>
public sealed record InstanceStructureReport
{
    public bool Reachable { get; init; }
    public string BasePath { get; init; } = "";
    public IReadOnlyList<StructureItem> Items { get; init; } = [];
    public string? Error { get; init; }
    public bool Ok { get; init; }
}

/// <summary>
/// Pre-flight readiness of an instance for a batch: the folder-skeleton inspection
/// (<see cref="Structure"/>) combined with a dry-run pairing of old vs new.
/// </summary>
public sealed record InstanceReadiness
{
    /// <summary>Folder-skeleton state: old/new/reports presence + PDF counts (+ files when requested).</summary>
    public InstanceStructureReport Structure { get; init; } = new();
    public bool Reachable { get; init; }
    public int OldPdfCount { get; init; }
    public int NewPdfCount { get; init; }
    public int Matched { get; init; }
    public int OnlyInOld { get; init; }
    public int OnlyInNew { get; init; }
    public IReadOnlyList<string> SampleOnlyInOld { get; init; } = [];
    public IReadOnlyList<string> SampleOnlyInNew { get; init; } = [];
    public bool Ready { get; init; }
    public string? Error { get; init; }
}

/// <summary>Lightweight job view.</summary>
public sealed record JobSummary
{
    public Guid Id { get; init; }
    public string BranchKey { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public JobStatus Status { get; init; }
    public double Progress { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }

    // Quick outcome (null until the job has a report) — drives the list verdict.
    public int? Differing { get; init; }
    public int? Errors { get; init; }
    public bool? GatePassed { get; init; }

    /// <summary>When the job was auto-recovered (crash / restart / stale worker); null if never. Drives the "Obnoveno" chip.</summary>
    public DateTimeOffset? RecoveredAt { get; init; }
}

/// <summary>Result for a single matched (or unmatched) file pair.</summary>
public sealed record FilePairResult
{
    public string RelativePath { get; init; } = "";
    public FilePairStatus Status { get; init; }
    public double Similarity { get; init; }
    public int DifferingPages { get; init; }
    public int ContentErrorCount { get; init; }
    public string? HighlightedPdfPath { get; init; }
    public string? Error { get; init; }

    /// <summary>Comparison-engine wall-clock in ms (probe+extract+render+highlight); null if no compare ran.</summary>
    public long? CompareMs { get; init; }
}

/// <summary>Outcome of <see cref="DiffPdfClient.CompareSinglePreviewAsync"/>: the verdict plus the highlighted
/// diff PDF bytes. <see cref="DiffPdf"/> is null when the documents are identical (nothing to highlight).</summary>
public sealed record SingleComparisonPreview
{
    public bool Identical { get; init; }
    public int OldPageCount { get; init; }
    public int NewPageCount { get; init; }
    public int DifferingPages { get; init; }
    /// <summary>Aggregate similarity 0–1, or NaN if the server did not report it.</summary>
    public double Similarity { get; init; }
    public int ContentErrorCount { get; init; }
    /// <summary>The highlighted diff PDF; null when identical (no diff was rendered).</summary>
    public byte[]? DiffPdf { get; init; }
}

/// <summary>Aggregate report for a whole batch run.</summary>
public sealed record BatchComparisonReport
{
    public string OldFolder { get; init; } = "";
    public string NewFolder { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public IReadOnlyList<FilePairResult> Files { get; init; } = [];
    public BatchGate? Gate { get; init; }
    public int Total { get; init; }
    public int Identical { get; init; }
    public int Differing { get; init; }
    public int OnlyInOld { get; init; }
    public int OnlyInNew { get; init; }
    public int Errors { get; init; }
    public int FilesWithContentErrors { get; init; }
    public IReadOnlyList<string> GateViolations { get; init; } = [];
    public bool Passed { get; init; }
}

/// <summary>CI-gate verdict (from <c>GET /jobs/{id}/result</c>).</summary>
public sealed record JobResult
{
    public bool Passed { get; init; }
    public bool Gated { get; init; }
    public IReadOnlyList<string> Violations { get; init; } = [];
    public JobResultSummary Summary { get; init; } = new();
}

public sealed record JobResultSummary
{
    public int Total { get; init; }
    public int Identical { get; init; }
    public int Differing { get; init; }
    public int OnlyInOld { get; init; }
    public int OnlyInNew { get; init; }
    public int Errors { get; init; }
    public int FilesWithContentErrors { get; init; }
}

/// <summary>A job's file-pair task view.</summary>
public sealed record FilePairTaskSummary
{
    public Guid Id { get; init; }
    public string RelativePath { get; init; } = "";
    public string Status { get; init; } = "";
    public int AttemptCount { get; init; }
    public string? ResultStatus { get; init; }
    public string? Error { get; init; }

    /// <summary>The full per-pair result once Completed (verdict + similarity + highlighted diff PDF path); null
    /// while pending/running. Present even while the owning job is still in flight, so a finished pair's diff can be opened.</summary>
    public FilePairResult? Result { get; init; }
}

/// <summary>Configured network: named shares + credential-profile names (never secrets).</summary>
public sealed record NetworkConfigSummary
{
    public IReadOnlyList<ShareInfo> Shares { get; init; } = [];
    public IReadOnlyList<string> CredentialProfiles { get; init; } = [];
}

public sealed record ShareInfo
{
    public string Name { get; init; } = "";
    public string? Root { get; init; }
    public string? LocalMountPath { get; init; }
    public bool RequiresCredentials { get; init; }
    public string? Description { get; init; }
}

/// <summary>OAuth2 token response from <c>POST /connect/token</c> (client-credentials).</summary>
public sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
}

/// <summary>One step of an automation as returned by the API.</summary>
public sealed record AutomationStepResponse
{
    public AutomationStepType Type { get; init; }
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>An automation as returned by the API. <c>NextRunAt</c> / <c>RunningSince</c> /
/// <c>ConsecutiveFailures</c> are engine state — read-only.</summary>
public sealed record AutomationResponse
{
    public Guid Id { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public AutomationScopeKind ScopeKind { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public IReadOnlyList<NotificationEvent> EventTriggers { get; init; } = [];
    public int EventDebounceSeconds { get; init; }
    public IReadOnlyList<AutomationStepResponse> Steps { get; init; } = [];
    public int TimeoutSeconds { get; init; }
    public int MaxAttempts { get; init; }
    public int RetryDelaySeconds { get; init; }
    public int FailureThreshold { get; init; }
    public IReadOnlyList<NotificationEvent> Events { get; init; } = [];
    public bool Enabled { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
    public DateTimeOffset? RunningSince { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public AutomationRunOutcome? LastOutcome { get; init; }
    public long Version { get; init; }

    /// <summary>The category this automation belongs to (derived from its dominant step).</summary>
    public AutomationCategory Category { get; init; }

    /// <summary>One-line purpose, derived from the automation's first step.</summary>
    public string Purpose { get; init; } = "";
}

/// <summary>One parameter an automation step accepts, as documented by the catalog.</summary>
public sealed record AutomationParameterSpecResponse
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";
    public AutomationParameterType Type { get; init; }
    public string? Default { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
    public IReadOnlyList<string>? EnumValues { get; init; }
}

/// <summary>Catalog entry for one step type: its category, human metadata and parameter schema.</summary>
public sealed record AutomationStepCatalogResponse
{
    public AutomationStepType Type { get; init; }
    public AutomationCategory Category { get; init; }
    public string DisplayName { get; init; } = "";
    public string Purpose { get; init; } = "";
    public IReadOnlyList<AutomationParameterSpecResponse> Parameters { get; init; } = [];
}

/// <summary>A ready-to-edit automation template as returned by the API.</summary>
public sealed record AutomationTemplateResponse
{
    public string Key { get; init; } = "";
    public AutomationCategory Category { get; init; }
    public string DisplayName { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string Icon { get; init; } = "";
    public AutomationScopeKind DefaultScope { get; init; }
    public IReadOnlyList<AutomationStepResponse> Steps { get; init; } = [];
    public string? RecommendedCron { get; init; }
    public int? RecommendedIntervalSeconds { get; init; }
    public IReadOnlyList<NotificationEvent> DefaultEvents { get; init; } = [];
}

/// <summary>One step result within an automation run as returned by the API.</summary>
public sealed record AutomationStepResultResponse
{
    public int Index { get; init; }
    public AutomationStepType Type { get; init; }
    public string? Name { get; init; }
    public AutomationRunOutcome Outcome { get; init; }
    public string Detail { get; init; } = "";
    public double DurationSeconds { get; init; }
}

/// <summary>One automation run (attempt) as returned by the API.</summary>
public sealed record AutomationRunResponse
{
    public Guid Id { get; init; }
    public Guid AutomationId { get; init; }
    public AutomationTriggerKind Trigger { get; init; }
    public string? TriggerDetail { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public AutomationRunOutcome Outcome { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<AutomationStepResultResponse> StepResults { get; init; } = [];
}

/// <summary>An e-mail notification rule as returned by the API.</summary>
public sealed record SubscriptionResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public IReadOnlyList<NotificationEvent> Events { get; init; } = [];
    public IReadOnlyList<string> BranchKeys { get; init; } = [];
    public IReadOnlyList<string> InstanceKeys { get; init; } = [];
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>SMTP settings as returned by the API. The password is never returned — only whether one is set.</summary>
public sealed record EmailSettingsResponse
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string? Username { get; init; }
    public bool HasPassword { get; init; }
    public string FromAddress { get; init; } = "";
    public string? FromName { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>Outcome of triggering a single instance.</summary>
public sealed record TriggerResult
{
    /// <summary>Launched / ScopeNotFound / NothingToCompare / Unreachable.</summary>
    public string Outcome { get; init; } = "";
    public Guid? JobId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Per-instance entry of a branch fan-out run.</summary>
public sealed record InstanceRunResult
{
    public string InstanceKey { get; init; } = "";
    public string Outcome { get; init; } = "";
    public Guid? JobId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Outcome of triggering every enabled instance under a branch.</summary>
public sealed record BranchRunResult
{
    public string BranchKey { get; init; } = "";
    public int Launched { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<InstanceRunResult> Instances { get; init; } = [];
}

// ---------------- Operational visibility ----------------

/// <summary>Identity, build version and uptime of the responding API replica.</summary>
public sealed record ReplicaInfo
{
    public string WorkerInstanceId { get; init; } = "";
    public string Version { get; init; } = "";
    public string Environment { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public double UptimeSeconds { get; init; }
}

/// <summary>Who currently holds the automation leader lease (from the shared database).</summary>
public sealed record LeaderInfo
{
    public string Role { get; init; } = "";
    public bool IsThisReplica { get; init; }
    public string? Owner { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }
    public DateTimeOffset? RenewedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool LeaseHealthy { get; init; }
}

/// <summary>Last activity of one automation background service on the responding replica.</summary>
public sealed record ServiceHealthInfo
{
    public string Service { get; init; } = "";
    public DateTimeOffset? LastTickAt { get; init; }
    public DateTimeOffset? LastLeaderActiveAt { get; init; }
    public long TickCount { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
}

/// <summary>Queue depth from the shared database.</summary>
public sealed record BacklogInfo
{
    public int QueuedJobs { get; init; }
    public int RunningJobs { get; init; }
    public int PausedJobs { get; init; }
    public int ActiveTasks { get; init; }
}

/// <summary>A single dependency check result.</summary>
public sealed record DependencyCheck
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Health of the external dependencies the server relies on.</summary>
public sealed record DependenciesInfo
{
    public DependencyCheck Database { get; init; } = new();
    public DependencyCheck Renderer { get; init; } = new();
    public DependencyCheck Storage { get; init; } = new();
}

/// <summary>Full operational status (from <c>GET /api/v1/status</c>).</summary>
public sealed record OperationalStatusResponse
{
    public ReplicaInfo Replica { get; init; } = new();
    public LeaderInfo Leader { get; init; } = new();
    public IReadOnlyList<ServiceHealthInfo> Services { get; init; } = [];
    public BacklogInfo Backlog { get; init; } = new();
    public int EnabledAutomations { get; init; }
    public DependenciesInfo Dependencies { get; init; } = new();
}

/// <summary>Readiness summary (from <c>GET /health/ready</c>; 200 ready / 503 degraded).</summary>
public sealed record ReadinessResponse
{
    public string Status { get; init; } = "";
    public IReadOnlyList<DependencyCheck> Checks { get; init; } = [];
}

/// <summary>Liveness info from <c>GET /health</c> (anonymous): build version, uptime, and whether the server requires auth.</summary>
public sealed record ServerInfo
{
    public string Status { get; init; } = "";
    public string Version { get; init; } = "";
    public double UptimeSeconds { get; init; }
    public bool AuthEnabled { get; init; }
}

// ---------------- Scope sync ----------------

/// <summary>How a branch folder reconciled against the database.</summary>
public enum BranchSyncState { Existing, Registered, Skipped, Error }

/// <summary>How an instance reconciled against the database / disk.</summary>
public enum InstanceSyncState { Existing, Registered, OrphanFolder, MissingFolder, OutOfRoot, Skipped, Error }

/// <summary>Reconciliation result for a single instance.</summary>
public sealed record InstanceSyncResult
{
    public string Key { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string BasePath { get; init; } = "";
    public InstanceSyncState State { get; init; }
    public InstanceStructureReport? Structure { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Reconciliation result for a single branch and the instances under it.</summary>
public sealed record BranchSyncResult
{
    public string Key { get; init; } = "";
    public string FolderName { get; init; } = "";
    public BranchSyncState State { get; init; }
    public IReadOnlyList<InstanceSyncResult> Instances { get; init; } = [];
    public string? Detail { get; init; }
}

/// <summary>Result of reconciling the scope root with the database (from <c>POST /api/v1/scope/sync</c>).</summary>
public sealed record ScopeSyncReport
{
    public bool Applied { get; init; }
    public string Root { get; init; } = "";
    public bool Reachable { get; init; }
    public IReadOnlyList<BranchSyncResult> Branches { get; init; } = [];
    public IReadOnlyList<InstanceSyncResult> MissingFolders { get; init; } = [];
    public IReadOnlyList<InstanceSyncResult> OutOfRoot { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>Live job progress pushed over SignalR (from <c>SubscribeToJobProgressAsync</c>).</summary>
public sealed record JobProgress
{
    public Guid JobId { get; init; }
    public string BranchKey { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public string Status { get; init; } = "";
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public double Progress { get; init; }
    public string? Error { get; init; }

    /// <summary>Set once the job has been auto-recovered after an interruption — raises the "Obnoveno" toast + chip.</summary>
    public DateTimeOffset? RecoveredAt { get; init; }
}
