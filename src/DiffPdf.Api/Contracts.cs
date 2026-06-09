using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;

namespace DiffPdf.Api;

/// <summary>A run-queue control request (enqueue/run/pause/resume/stop) for an instance or a whole branch.</summary>
public sealed record QueueActionRequest(QueueAction Action);

/// <summary>Request body for comparing a single old/new PDF pair.</summary>
public sealed record SingleComparisonRequest
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public ComparisonOptions Options { get; init; } = new();
}

/// <summary>Configured network: named shares and credential-profile names (never secrets).</summary>
public sealed record NetworkConfigSummary(IReadOnlyList<ShareInfo> Shares, IReadOnlyList<string> CredentialProfiles);

public sealed record CreateBranchRequest(string Key, string Name);

/// <summary>Update a branch's mutable fields (the Key is its immutable identity). <paramref name="Version"/> guards concurrent edits.</summary>
public sealed record UpdateBranchRequest(string Name, bool Enabled, long Version);

/// <summary>A branch plus its instance count and live run-queue state — lets the list view render in one call.</summary>
public sealed record BranchSummary(Branch Branch, int InstanceCount, BranchQueueState Queue);

/// <summary>Create an instance under a branch. <paramref name="BasePath"/> holds the old/new/reports subfolders.</summary>
public sealed record CreateInstanceRequest(string Key, string Name, string BasePath, string? CredentialProfile = null);

/// <summary>Update an instance's mutable fields (the Key is its immutable identity). <paramref name="Version"/> guards concurrent edits.</summary>
public sealed record UpdateInstanceRequest(string Name, string BasePath, string? CredentialProfile, bool Enabled, long Version);

/// <summary>The configured structure root under which branches/instances are created server-side (null when not configured).</summary>
public sealed record ScopeRootInfo(string? Root, bool Configured);

// ---------------- Triggers (Spouštěče) ----------------

/// <summary>Create a trigger bound to a branch+instance.</summary>
public sealed record CreateTriggerRequest(
    string BranchKey, string InstanceKey, string Name, string? Description = null, bool Enabled = true);

/// <summary>Partial update of a trigger (null = leave unchanged). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateTriggerRequest(
    string? Name = null, string? Description = null, bool? Enabled = null, long? Version = null);

/// <summary>A trigger as returned by the API.</summary>
public sealed record TriggerResponse(
    Guid Id, Guid BranchId, Guid InstanceId, string BranchKey, string InstanceKey,
    string Name, string? Description, TriggerActionType ActionType, TriggerStatus Status,
    bool Enabled, bool IsDefault, int RunCount, DateTimeOffset? LastRunAt, string? LastOutcome,
    string? CreatedBy, string? UpdatedBy, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    bool IsDeleted, DateTimeOffset? DeletedAt, long Version)
{
    public static TriggerResponse From(Trigger t) => new(
        t.Id, t.BranchId, t.InstanceId, t.BranchKey, t.InstanceKey, t.Name, t.Description, t.ActionType, t.Status,
        t.Enabled, t.IsDefault, t.RunCount, t.LastRunAt, t.LastOutcome, t.CreatedBy, t.UpdatedBy,
        t.CreatedAt, t.UpdatedAt, t.IsDeleted, t.DeletedAt, t.Version);
}

/// <summary>One trigger run as returned by the API.</summary>
public sealed record TriggerRunResponse(
    Guid Id, Guid TriggerId, Guid? BatchJobId, JobSource Source, string Status, string? Result,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, string? Error, string? RequestedBy)
{
    public static TriggerRunResponse From(TriggerRun r) => new(
        r.Id, r.TriggerId, r.BatchJobId, r.Source, r.Status, r.Result, r.StartedAt, r.FinishedAt, r.DurationMs, r.Error, r.RequestedBy);
}

/// <summary>Result of running a trigger (the run does not wait for the comparison to finish).</summary>
public sealed record RunTriggerResponse(bool Success, Guid TriggerId, Guid? BatchJobId, string Status, string Message, string? ErrorCode);

/// <summary>A trigger management error (no internal details leaked).</summary>
public sealed record TriggerErrorResponse(bool Success, string ErrorCode, string Message);

/// <summary>One audit entry as returned by the API.</summary>
public sealed record AuditEntryResponse(
    Guid Id, DateTimeOffset At, string? Actor, string Source, string Action, string EntityType, string? EntityId, string? Detail)
{
    public static AuditEntryResponse From(AuditEntry a) => new(a.Id, a.At, a.Actor, a.Source, a.Action, a.EntityType, a.EntityId, a.Detail);
}

/// <summary>Response after creating an instance: the record plus the result of provisioning its folder skeleton (null when skipped).</summary>
public sealed record CreatedInstanceResponse(ComparisonInstance Instance, InstanceStructureReport? Structure);

// ---------------- Scope configuration (triggers + comparers) ----------------

/// <summary>
/// Upsert a scope's configuration: the source selectors plus this level's custom payloads. <see cref="Version"/>
/// guards concurrent edits (null = overwrite the latest). Sources are validated against the level server-side.
/// </summary>
public sealed record UpsertScopeConfigRequest(
    ConfigSource TriggerSource,
    TriggerConfig TriggerConfig,
    ConfigSource ComparerSource,
    ComparisonOptions ComparisonOptions,
    long? Version = null);

/// <summary>
/// A scope's stored configuration together with the server-resolved effective configuration. The client binds
/// the stored sources/payloads for editing, and displays the effective values plus the level each resolved
/// from (<see cref="TriggerResolvedFrom"/> / <see cref="ComparerResolvedFrom"/>) — it performs no inheritance itself.
/// </summary>
public sealed record ScopeConfigResponse(
    ConfigScopeLevel Level,
    string? BranchKey,
    string? InstanceKey,
    ConfigSource TriggerSource,
    TriggerConfig TriggerConfig,
    ConfigSource ComparerSource,
    ComparisonOptions ComparisonOptions,
    TriggerConfig EffectiveTriggerConfig,
    ConfigScopeLevel TriggerResolvedFrom,
    ComparisonOptions EffectiveComparisonOptions,
    ConfigScopeLevel ComparerResolvedFrom,
    DateTimeOffset? UpdatedAt,
    long Version);

// ---------------- Scheduled runs (gear "Konfigurace" → Plán běhu) ----------------

/// <summary>Enable/disable a scope's scheduled comparison. <see cref="Cron"/> is a 5-field UTC cron (required when enabled).</summary>
public sealed record SetScheduleRequest(bool Enabled, string? Cron);

/// <summary>A scope's current scheduled-run setting.</summary>
public sealed record ScheduleResponse(bool Enabled, string? Cron);

// Per-scope statistics (branch / instance detail): the stat records + ScopeStatsService now live in
// DiffPdf.Application.Stats (the endpoint returns that ScopeStatsResponse directly — identical JSON).

/// <summary>
/// Pre-flight readiness of an instance for a batch. Combines the folder-skeleton
/// inspection (<see cref="Structure"/>: old/new/reports state + PDF counts, optionally
/// the file lists) with a dry-run pairing of old vs new. <see cref="Ready"/> mirrors the
/// batch gate (both input folders must hold at least one PDF).
/// </summary>
public sealed record InstanceReadiness(
    InstanceStructureReport Structure,
    int Matched,
    int OnlyInOld,
    int OnlyInNew,
    IReadOnlyList<string> SampleOnlyInOld,
    IReadOnlyList<string> SampleOnlyInNew,
    bool Ready,
    string? Error)
{
    /// <summary>Whether the instance base path was reachable.</summary>
    public bool Reachable => Structure.Reachable;

    /// <summary>Number of *.pdf files in the <c>old</c> input folder.</summary>
    public int OldPdfCount => Structure.OldPdfCount;

    /// <summary>Number of *.pdf files in the <c>new</c> input folder.</summary>
    public int NewPdfCount => Structure.NewPdfCount;
}

/// <summary>Per-file-pair task view.</summary>
public sealed record FilePairTaskSummary
{
    public required Guid Id { get; init; }
    public required string RelativePath { get; init; }
    public required string Status { get; init; }
    public int AttemptCount { get; init; }
    public string? ResultStatus { get; init; }
    public string? Error { get; init; }

    /// <summary>The full per-pair result once the task is Completed (verdict + similarity + the highlighted diff
    /// PDF path) — null while pending/running. Lets the client open a finished pair's diff mid-run, before the
    /// whole-job report exists.</summary>
    public FilePairResult? Result { get; init; }

    public static FilePairTaskSummary From(FilePairTask t) => new()
    {
        Id = t.Id,
        RelativePath = t.RelativePath,
        Status = t.Status.ToString(),
        AttemptCount = t.AttemptCount,
        ResultStatus = t.Result?.Status.ToString(),
        Error = t.Error,
        Result = t.Result,
    };
}

/// <summary>Lightweight job view returned by the API.</summary>
public sealed record JobSummary
{
    public required Guid Id { get; init; }
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }
    public required string Status { get; init; }
    public double Progress { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }

    // Quick outcome from the job's report (null until it has one) — lets the list show a verdict without opening it.
    public int? Differing { get; init; }
    public int? Errors { get; init; }
    public bool? GatePassed { get; init; }

    /// <summary>When the job was last auto-recovered (crash / restart / stale worker); null if never. Drives the "Obnoveno" chip.</summary>
    public DateTimeOffset? RecoveredAt { get; init; }

    public static JobSummary From(ComparisonJob job) => new()
    {
        Id = job.Id,
        BranchKey = job.BranchKey,
        InstanceKey = job.InstanceKey,
        Status = job.Status.ToString(),
        Progress = job.Progress,
        ProcessedCount = job.ProcessedCount,
        TotalCount = job.TotalCount,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
        Error = job.Error,
        Differing = job.Report?.Differing,
        Errors = job.Report?.Errors,
        GatePassed = job.Report?.Passed,
        RecoveredAt = job.RecoveredAt,
    };

    /// <summary>From the lightweight list projection (verdict already denormalized; no report deserialized).</summary>
    public static JobSummary From(JobListItem job) => new()
    {
        Id = job.Id,
        BranchKey = job.BranchKey,
        InstanceKey = job.InstanceKey,
        Status = job.Status.ToString(),
        Progress = job.Progress,
        ProcessedCount = job.ProcessedCount,
        TotalCount = job.TotalCount,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
        Error = job.Error,
        Differing = job.Differing,
        Errors = job.Errors,
        GatePassed = job.GatePassed,
        RecoveredAt = job.RecoveredAt,
    };
}

// ---------------- Automation: notification subscriptions ----------------

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

/// <summary>A subscription as returned by the API.</summary>
public sealed record SubscriptionResponse
{
    public required Guid Id { get; init; }
    public required string Channel { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }

    public static SubscriptionResponse From(NotificationSubscription s) => new()
    {
        Id = s.Id,
        Channel = s.Channel,
        Target = s.Target,
        Events = s.Events,
        BranchKey = s.BranchKey,
        InstanceKey = s.InstanceKey,
        Enabled = s.Enabled,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        Version = s.Version,
    };
}

// ---------------- Automation: on-demand triggers ----------------

/// <summary>Result of triggering a single instance: the launch outcome and the new job id (when launched).</summary>
public sealed record TriggerResult(string Outcome, Guid? JobId, string? Detail);

/// <summary>Per-instance entry in a branch fan-out run.</summary>
public sealed record InstanceRunResult(string InstanceKey, string Outcome, Guid? JobId, string? Detail);

/// <summary>Result of triggering every enabled instance under a branch.</summary>
public sealed record BranchRunResult(string BranchKey, int Launched, int Skipped, IReadOnlyList<InstanceRunResult> Instances);

// ---------------- Operational visibility ----------------

/// <summary>Identity, build version and uptime of the responding API replica.</summary>
public sealed record ReplicaInfo(string WorkerInstanceId, string Version, string Environment, DateTimeOffset StartedAt, double UptimeSeconds);

/// <summary>Who currently holds the automation leader lease (read from the shared database).</summary>
public sealed record LeaderInfo(
    string Role, bool IsThisReplica, string? Owner,
    DateTimeOffset? AcquiredAt, DateTimeOffset? RenewedAt, DateTimeOffset? ExpiresAt, bool LeaseHealthy);

/// <summary>Last activity of one automation background service on this replica.</summary>
public sealed record ServiceHealthInfo(
    string Service, DateTimeOffset? LastTickAt, DateTimeOffset? LastLeaderActiveAt,
    long TickCount, string? LastError, DateTimeOffset? LastErrorAt);

/// <summary>Queue depth from the shared database.</summary>
public sealed record BacklogInfo(int QueuedJobs, int RunningJobs, int PausedJobs, int ActiveTasks);

/// <summary>A single dependency check result.</summary>
public sealed record DependencyCheck(string Name, bool Ok, string? Detail);

/// <summary>Health of the external dependencies the server relies on.</summary>
public sealed record DependenciesInfo(DependencyCheck Database, DependencyCheck Renderer, DependencyCheck Storage);

/// <summary>
/// Full operational status (authenticated). <see cref="Replica"/>, <see cref="Services"/> and the
/// renderer/storage checks are <b>per-replica</b> (the responding process); <see cref="Leader"/> and
/// <see cref="Backlog"/> are <b>shared</b> (read from the database).
/// </summary>
public sealed record OperationalStatusResponse(
    ReplicaInfo Replica,
    LeaderInfo Leader,
    IReadOnlyList<ServiceHealthInfo> Services,
    BacklogInfo Backlog,
    int EnabledChecks,
    DependenciesInfo Dependencies);

/// <summary>Readiness summary: overall status plus the per-check breakdown (200 ready / 503 degraded).</summary>
public sealed record ReadinessResponse(string Status, IReadOnlyList<DependencyCheck> Checks);
