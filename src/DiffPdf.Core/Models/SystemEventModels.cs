namespace DiffPdf.Core.Models;

/// <summary>Severity of a <see cref="SystemEvent"/> — drives the client's notification-center rendering.</summary>
public enum SystemEventSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One row of the append-only system event log — the durable trail of everything the server did or noticed:
/// job outcomes, automation runs, notification dead-letters, recovery interventions. <see cref="Seq"/> is a
/// monotonic cursor (DB identity): clients remember the last Seq they saw and replay anything newer after a
/// reconnect, so a dropped realtime push can never lose an event permanently.
/// </summary>
public sealed record SystemEvent
{
    /// <summary>Monotonic cursor assigned by the store on append (0 until persisted).</summary>
    public long Seq { get; init; }

    /// <summary>Dotted machine type, e.g. <c>job.completed</c>, <c>automation.run.finished</c> (see <see cref="SystemEventTypes"/>).</summary>
    public required string Type { get; init; }

    public SystemEventSeverity Severity { get; init; } = SystemEventSeverity.Info;

    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public Guid? JobId { get; init; }
    public Guid? AutomationId { get; init; }

    /// <summary>Human-readable message (Czech), shown verbatim in the client.</summary>
    public required string Message { get; init; }

    /// <summary>Optional extra detail (error text, counts), shown expanded/tooltipped.</summary>
    public string? Detail { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Well-known <see cref="SystemEvent.Type"/> values (dotted; prefix-filterable, e.g. <c>job.</c>).</summary>
public static class SystemEventTypes
{
    public const string JobCompleted = "job.completed";
    public const string JobCompletedWithErrors = "job.completed-with-errors";
    public const string JobGateViolated = "job.gate-violated";
    public const string JobFailed = "job.failed";
    public const string JobStalled = "job.stalled";
    public const string JobRecovered = "job.recovered";
    public const string AutomationRunFinished = "automation.run.finished";
    public const string NotificationDeadLetter = "notification.deadletter";
    public const string RecoveryOrphans = "recovery.orphans";
}
