namespace DiffPdf.Core.Models;

/// <summary>The kind of event a notification announces — a batch outcome or a control-check result.</summary>
public enum NotificationEvent
{
    /// <summary>Batch completed and (if a gate was configured) passed it.</summary>
    Completed,

    /// <summary>Batch completed but violated its CI gate.</summary>
    GateViolated,

    /// <summary>Batch ended in a failed state.</summary>
    Failed,

    /// <summary>A readiness control check failed (an instance's inputs are not ready to compare).</summary>
    ReadinessFailed,

    /// <summary>A health control check found a critical dependency degraded (database, renderer, storage).</summary>
    HealthDegraded,

    /// <summary>A structure-sync control check found the scope/filesystem out of sync.</summary>
    StructureDrift,

    /// <summary>A previously failing control check passed again.</summary>
    CheckRecovered,
}

/// <summary>
/// A subscription that routes finished-batch notifications to a delivery target. Managed
/// at runtime through the API and persisted in the store — not bound from configuration.
/// </summary>
public sealed record NotificationSubscription
{
    public required Guid Id { get; init; }

    /// <summary>Delivery channel: <c>webhook</c> (Slack/Teams/generic JSON POST) or <c>smtp</c> (e-mail).</summary>
    public required string Channel { get; init; }

    /// <summary>The webhook URL or the recipient e-mail address, depending on <see cref="Channel"/>.</summary>
    public required string Target { get; init; }

    /// <summary>Events this subscription cares about. Empty = none.</summary>
    public required IReadOnlyList<NotificationEvent> Events { get; init; }

    /// <summary>Optional branch filter (case-insensitive); null/empty = any branch.</summary>
    public string? BranchKey { get; init; }

    /// <summary>Optional instance filter (case-insensitive); null/empty = any instance.</summary>
    public string? InstanceKey { get; init; }

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}
