using DiffPdf.Core.Models;
using DiffPdf.Core.Network;

namespace DiffPdf.Api;

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

/// <summary>Create an instance under a branch. <paramref name="BasePath"/> holds the old/new/reports subfolders.</summary>
public sealed record CreateInstanceRequest(string Key, string Name, string BasePath, string? CredentialProfile = null);

/// <summary>Response after creating an instance: the record plus the result of provisioning its folder skeleton (null when skipped).</summary>
public sealed record CreatedInstanceResponse(ComparisonInstance Instance, InstanceStructureReport? Structure);

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

    public static FilePairTaskSummary From(FilePairTask t) => new()
    {
        Id = t.Id,
        RelativePath = t.RelativePath,
        Status = t.Status.ToString(),
        AttemptCount = t.AttemptCount,
        ResultStatus = t.Result?.Status.ToString(),
        Error = t.Error,
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
    };
}

// ---------------- Automation: schedules ----------------

/// <summary>Create a schedule under an instance. It runs the instance's old/new folders on its cron with the given options/gate.</summary>
public sealed record CreateScheduleRequest
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public required string Cron { get; init; }
    public ComparisonOptions Options { get; init; } = new();
    public BatchGate? Gate { get; init; }
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int MaxDegreeOfParallelism { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Update a schedule. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateScheduleRequest
{
    public string? Name { get; init; }
    public required string Cron { get; init; }
    public ComparisonOptions Options { get; init; } = new();
    public BatchGate? Gate { get; init; }
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int MaxDegreeOfParallelism { get; init; }
    public bool Enabled { get; init; } = true;
    public required long Version { get; init; }
}

/// <summary>A schedule as returned by the API.</summary>
public sealed record ScheduleResponse
{
    public required Guid Id { get; init; }
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Cron { get; init; }
    public required ComparisonOptions Options { get; init; }
    public BatchGate? Gate { get; init; }
    public required string SearchPattern { get; init; }
    public bool Recursive { get; init; }
    public int MaxDegreeOfParallelism { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public long Version { get; init; }

    public static ScheduleResponse From(ComparisonSchedule s) => new()
    {
        Id = s.Id,
        BranchKey = s.BranchKey,
        InstanceKey = s.InstanceKey,
        Key = s.Key,
        Name = s.Name,
        Cron = s.Cron,
        Options = s.Options,
        Gate = s.Gate,
        SearchPattern = s.SearchPattern,
        Recursive = s.Recursive,
        MaxDegreeOfParallelism = s.MaxDegreeOfParallelism,
        Enabled = s.Enabled,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        LastRunAt = s.LastRunAt,
        Version = s.Version,
    };
}

/// <summary>One run of a schedule (the batch it launched) as returned by the API.</summary>
public sealed record ScheduleRunResponse
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string Outcome { get; init; }
    public int Differing { get; init; }
    public int Errors { get; init; }
    public int FilesWithContentErrors { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<string> GateViolations { get; init; } = [];
    public string? Error { get; init; }

    public static ScheduleRunResponse From(ScheduleRun r) => new()
    {
        Id = r.Id,
        JobId = r.JobId,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        Outcome = r.Outcome.ToString(),
        Differing = r.Differing,
        Errors = r.Errors,
        FilesWithContentErrors = r.FilesWithContentErrors,
        Passed = r.Passed,
        GateViolations = r.GateViolations,
        Error = r.Error,
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

// ---------------- Automation: folder-watch ----------------

/// <summary>Create or replace an instance's folder-watch.</summary>
public sealed record SetWatchRequest
{
    public int StabilitySeconds { get; init; } = 30;
    public bool Enabled { get; init; } = true;
}

/// <summary>An instance's folder-watch as returned by the API.</summary>
public sealed record WatchResponse
{
    public required Guid Id { get; init; }
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }
    public int StabilitySeconds { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? LastTriggeredAt { get; init; }
    public long Version { get; init; }

    public static WatchResponse From(FolderWatch w) => new()
    {
        Id = w.Id,
        BranchKey = w.BranchKey,
        InstanceKey = w.InstanceKey,
        StabilitySeconds = w.StabilitySeconds,
        Enabled = w.Enabled,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
        LastTriggeredAt = w.LastTriggeredAt,
        Version = w.Version,
    };
}

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
    int EnabledSchedules,
    int EnabledWatches,
    bool RetentionEnabled,
    DependenciesInfo Dependencies);

/// <summary>Readiness summary: overall status plus the per-check breakdown (200 ready / 503 degraded).</summary>
public sealed record ReadinessResponse(string Status, IReadOnlyList<DependencyCheck> Checks);
