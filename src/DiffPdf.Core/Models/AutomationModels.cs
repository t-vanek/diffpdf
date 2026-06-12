namespace DiffPdf.Core.Models;

/// <summary>The kind of work one <see cref="AutomationStep"/> performs.</summary>
public enum AutomationStepType
{
    /// <summary>Verifies an instance's inputs are ready to compare (old/new folders, pairing).</summary>
    Readiness,

    /// <summary>Verifies the server's critical dependencies (database, renderer, storage).</summary>
    Health,

    /// <summary>Reconciles a filesystem root with the branch/instance scope in the database.</summary>
    StructureSync,

    /// <summary>Prunes on-disk report artifacts of finished jobs older than a retention window.</summary>
    Retention,

    /// <summary>Prunes old database rows (finished jobs + their tasks, trigger-run, audit and automation-run history) older than a retention window.</summary>
    DbRowRetention,

    /// <summary>Enqueues a comparison for each enabled instance in scope.</summary>
    ScheduledComparison,

    /// <summary>Monitors a system resource (disk free space, CPU load or RAM usage) against thresholds.
    /// The concrete resource is chosen by the <c>resource</c> parameter (<c>disk</c> / <c>cpu</c> / <c>ram</c>).</summary>
    SystemResource,

    /// <summary>Monitors the comparison queue: backlog depth (queued jobs) and jobs running suspiciously long.</summary>
    QueueHealth,

    /// <summary>Monitors data freshness: warns when an in-scope instance has produced no successful comparison
    /// within a window (a silently stalled pipeline).</summary>
    ComparisonFreshness,

    /// <summary>Re-enqueues a fresh comparison for in-scope instances whose most recent batch failed
    /// (job-level failure, and optionally pair-level errors) — recovers from transient failures.</summary>
    ReRunFailed,

    /// <summary>Mirrors files from a configured source folder (local / UNC / <c>share:</c>) into each in-scope
    /// instance's <c>new/</c> (or <c>old/</c>) folder, so a drop location feeds the comparison inputs.</summary>
    FolderSync,
}

/// <summary>How widely an <see cref="Automation"/> applies.</summary>
public enum AutomationScopeKind
{
    /// <summary>Applies to the whole server (no branch/instance target).</summary>
    Global,

    /// <summary>Applies to every enabled instance under a branch.</summary>
    Branch,

    /// <summary>Applies to a single instance.</summary>
    Instance,
}

/// <summary>Outcome of one run (or one step) of an automation.</summary>
public enum AutomationRunOutcome
{
    /// <summary>The run passed.</summary>
    Ok,

    /// <summary>The run found a non-fatal issue worth surfacing.</summary>
    Warning,

    /// <summary>The run failed.</summary>
    Failed,
}

/// <summary>What caused an <see cref="AutomationRun"/> to start.</summary>
public enum AutomationTriggerKind
{
    /// <summary>Fired by the engine on the automation's cron/interval cadence.</summary>
    Schedule,

    /// <summary>Fired by a matching domain notification event (see <see cref="Automation.EventTriggers"/>).</summary>
    Event,

    /// <summary>Fired explicitly through the run-now API.</summary>
    Manual,
}

/// <summary>One step of an automation's pipeline; executed in order by the engine.</summary>
public sealed record AutomationStep
{
    /// <summary>What the step does.</summary>
    public required AutomationStepType Type { get; init; }

    /// <summary>Optional display label (shown in run history); defaults to the type name.</summary>
    public string? Name { get; init; }

    /// <summary>Type-specific parameters (e.g. <c>retentionDays</c>). Interpreted by the step executor.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A named automation, managed at runtime through the API and persisted in the store — not bound from
/// configuration. The automation engine fires it from any combination of triggers — a cron/interval
/// schedule, domain notification <see cref="EventTriggers"/> (debounced), or manually — and executes its
/// <see cref="Steps"/> in order, failing fast on a <see cref="AutomationRunOutcome.Failed"/> step
/// (a Warning continues). Non-Ok outcomes raise the configured notification <see cref="Events"/>;
/// <see cref="FailureThreshold"/> consecutive failures escalate once via
/// <see cref="NotificationEvent.AutomationFailing"/>.
/// </summary>
public sealed record Automation
{
    public required Guid Id { get; init; }

    /// <summary>Key identifying this automation (used in REST routes); unique across the server.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>How widely the automation applies (steps that resolve instances honour this).</summary>
    public AutomationScopeKind ScopeKind { get; init; } = AutomationScopeKind.Global;

    /// <summary>Branch target for <see cref="AutomationScopeKind.Branch"/> / <see cref="AutomationScopeKind.Instance"/>.</summary>
    public string? BranchKey { get; init; }

    /// <summary>Instance target for <see cref="AutomationScopeKind.Instance"/>.</summary>
    public string? InstanceKey { get; init; }

    /// <summary>Standard 5-field cron expression evaluated in UTC; wins over <see cref="IntervalSeconds"/>.</summary>
    public string? Cron { get; init; }

    /// <summary>Schedule cadence in seconds, used when <see cref="Cron"/> is null.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>Domain events that fire this automation. Empty = not event-triggered.
    /// With no schedule either, the automation is manual-only.</summary>
    public IReadOnlyList<NotificationEvent> EventTriggers { get; init; } = [];

    /// <summary>Minimum seconds between two event-triggered runs (claims are atomic across replicas).</summary>
    public int EventDebounceSeconds { get; init; } = 60;

    /// <summary>Ordered step pipeline; at least one step.</summary>
    public required IReadOnlyList<AutomationStep> Steps { get; init; }

    /// <summary>Wall-clock cap per attempt; an attempt past it fails with a timeout result.</summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>How many times a failed pipeline is attempted per run (1 = no retry).</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Delay between attempts.</summary>
    public int RetryDelaySeconds { get; init; } = 30;

    /// <summary>Consecutive failed runs that escalate once via <see cref="NotificationEvent.AutomationFailing"/>.</summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>Notification events raised when a run ends non-Ok. Empty = no notification.</summary>
    public IReadOnlyList<NotificationEvent> Events { get; init; } = [];

    public bool Enabled { get; init; } = true;

    /// <summary>Mute switch for this automation's outbound notifications: when false the runner raises no
    /// notification events (no e-mails, no event-trigger cascade) — the automation still runs and its runs
    /// stay visible in the history + system event log. Toggled from the UI without touching the Version.</summary>
    public bool NotificationsEnabled { get; init; } = true;

    // -- Persisted engine state (read-only through the API) --

    /// <summary>Next scheduled due time (UTC); null = no schedule or not yet seeded. Persisted so the
    /// schedule survives restarts and leader failover.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Set while a run is in flight (overlap guard); null = idle. A stale value (older than the
    /// run's worst-case duration) is reclaimed, so a crash never wedges the automation.</summary>
    public DateTimeOffset? RunningSince { get; init; }

    /// <summary>Consecutive failed runs so far; reset by any non-failed run.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>When an event trigger last claimed this automation — the durable debounce anchor.</summary>
    public DateTimeOffset? LastEventTriggeredAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When this automation last ran; null until first run.</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>Outcome of the most recent run; null until first run.</summary>
    public AutomationRunOutcome? LastOutcome { get; init; }

    public long Version { get; init; } = 1;
}

/// <summary>Result of one executed step within an <see cref="AutomationRun"/>.</summary>
public sealed record AutomationStepResult(
    int Index,
    AutomationStepType Type,
    string? Name,
    AutomationRunOutcome Outcome,
    string Detail,
    double DurationSeconds);

/// <summary>
/// One attempt of an <see cref="Automation"/>: what triggered it, its outcome and the per-step results.
/// Recorded by the runner (one row per attempt); <see cref="AutomationId"/> is not a foreign key, so run
/// history outlives a deleted automation.
/// </summary>
public sealed record AutomationRun
{
    public required Guid Id { get; init; }
    public required Guid AutomationId { get; init; }

    /// <summary>What started this run.</summary>
    public AutomationTriggerKind Trigger { get; init; } = AutomationTriggerKind.Schedule;

    /// <summary>Human-readable trigger context (e.g. <c>event:Failed</c>, <c>manual</c>).</summary>
    public string? TriggerDetail { get; init; }

    /// <summary>1-based attempt number within the run (up to <see cref="Automation.MaxAttempts"/>).</summary>
    public int Attempt { get; init; } = 1;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public AutomationRunOutcome Outcome { get; init; } = AutomationRunOutcome.Ok;

    /// <summary>Composed human-readable summary of the step results.</summary>
    public string? Detail { get; init; }

    /// <summary>Per-step results in pipeline order (steps after a failed one are absent).</summary>
    public IReadOnlyList<AutomationStepResult> StepResults { get; init; } = [];
}
