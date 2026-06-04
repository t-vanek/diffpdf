namespace DiffPdf.Core.Models;

/// <summary>The kind of automated control/monitoring check a <see cref="ControlCheck"/> performs.</summary>
public enum CheckType
{
    /// <summary>Verifies an instance's inputs are ready to compare (old/new folders, pairing).</summary>
    Readiness,

    /// <summary>Verifies the server's critical dependencies (database, renderer, storage).</summary>
    Health,

    /// <summary>Reconciles a filesystem root with the branch/instance scope in the database.</summary>
    StructureSync,

    /// <summary>Prunes on-disk report artifacts of finished jobs older than a retention window.</summary>
    Retention,
}

/// <summary>How widely a <see cref="ControlCheck"/> applies.</summary>
public enum CheckScopeKind
{
    /// <summary>Applies to the whole server (no branch/instance target).</summary>
    Global,

    /// <summary>Applies to every enabled instance under a branch.</summary>
    Branch,

    /// <summary>Applies to a single instance.</summary>
    Instance,
}

/// <summary>Outcome of one execution of a control check.</summary>
public enum CheckRunOutcome
{
    /// <summary>The check passed.</summary>
    Ok,

    /// <summary>The check found a non-fatal issue worth surfacing.</summary>
    Warning,

    /// <summary>The check failed.</summary>
    Failed,
}

/// <summary>
/// A named automated control/monitoring check, managed at runtime through the API and persisted in the
/// store — not bound from configuration. A unified background runner executes enabled checks on their
/// cadence (cron or interval) and raises the configured notification <see cref="Events"/> on failure.
/// </summary>
public sealed record ControlCheck
{
    public required Guid Id { get; init; }

    /// <summary>Key identifying this check (used in REST routes); unique across the server.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>What the check does.</summary>
    public required CheckType Type { get; init; }

    /// <summary>How widely the check applies.</summary>
    public CheckScopeKind ScopeKind { get; init; } = CheckScopeKind.Global;

    /// <summary>Branch target for <see cref="CheckScopeKind.Branch"/> / <see cref="CheckScopeKind.Instance"/>.</summary>
    public string? BranchKey { get; init; }

    /// <summary>Instance target for <see cref="CheckScopeKind.Instance"/>.</summary>
    public string? InstanceKey { get; init; }

    /// <summary>Standard 5-field cron expression evaluated in UTC. When null, <see cref="IntervalSeconds"/> is used.</summary>
    public string? Cron { get; init; }

    /// <summary>Polling cadence in seconds, used when <see cref="Cron"/> is null.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>Type-specific parameters (e.g. <c>retentionDays</c>, <c>rootPath</c>). Interpreted by the executor.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Notification events raised when the check fails. Empty = no notification.</summary>
    public IReadOnlyList<NotificationEvent> Events { get; init; } = [];

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When this check last ran; null until first run.</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>Outcome of the most recent run; null until first run.</summary>
    public CheckRunOutcome? LastOutcome { get; init; }

    public long Version { get; init; } = 1;
}

/// <summary>
/// One execution of a <see cref="ControlCheck"/>: its outcome and a human-readable detail. Recorded by
/// the runner; <see cref="CheckId"/> is not a foreign key, so run history outlives a deleted check.
/// </summary>
public sealed record ControlCheckRun
{
    public required Guid Id { get; init; }
    public required Guid CheckId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public CheckRunOutcome Outcome { get; init; } = CheckRunOutcome.Ok;
    public string? Detail { get; init; }
}
