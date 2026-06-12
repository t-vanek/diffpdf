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

    /// <summary>A previously failing automation passed again.</summary>
    AutomationRecovered,

    /// <summary>A Running comparison job stopped making progress (stalled) past the watchdog's stall window.</summary>
    JobStalled,

    /// <summary>A batch completed (gate passed, or no gate) but some file pairs could not be compared — they
    /// errored, e.g. after exhausting their retries or an unreadable PDF. Escalated so the failures are not
    /// announced silently under a plain <see cref="Completed"/>.</summary>
    CompletedWithErrors,

    /// <summary>An automation has failed its configured number of consecutive runs (escalation; raised once
    /// when the failure streak crosses the threshold).</summary>
    AutomationFailing,
}

/// <summary>
/// An e-mail notification rule, managed at runtime through the API and persisted in the store (not bound from
/// configuration). A finished-batch or control-check notification is delivered to every address in
/// <see cref="Recipients"/> when the rule is enabled, its <see cref="Events"/> include the event, and its scope
/// (<see cref="BranchKeys"/> / <see cref="InstanceKeys"/>) matches.
/// </summary>
public sealed record NotificationSubscription
{
    public required Guid Id { get; init; }

    /// <summary>Optional human label for the rule (shown in the UI list); not used for matching.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Recipient e-mail addresses. Must be non-empty.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>Events this rule fires on. Empty = none.</summary>
    public required IReadOnlyList<NotificationEvent> Events { get; init; }

    /// <summary>Branch-key filter (case-insensitive); empty = any branch.</summary>
    public IReadOnlyList<string> BranchKeys { get; init; } = [];

    /// <summary>Instance-key filter (case-insensitive); empty = any instance.</summary>
    public IReadOnlyList<string> InstanceKeys { get; init; } = [];

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}

/// <summary>
/// SMTP transport + sender account for outbound e-mail notifications. Managed at runtime through the API and
/// persisted as a single row — replaces the appsettings-only <c>Notifications:Smtp</c> (still honored as a
/// fallback until settings are saved). The <see cref="Password"/> is stored server-side and never returned to
/// clients (the API surfaces only whether one is set).
/// </summary>
public sealed record EmailSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;

    /// <summary>When true MailKit picks the right transport security for the port (implicit SSL on 465, STARTTLS
    /// otherwise); false connects in plain text (a local/relay SMTP without TLS).</summary>
    public bool UseSsl { get; init; } = true;

    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>Envelope/From address mails are sent from.</summary>
    public string FromAddress { get; init; } = "diffpdf@localhost";

    /// <summary>Optional display name shown alongside <see cref="FromAddress"/>.</summary>
    public string? FromName { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;

    /// <summary>True when an SMTP host is configured — the transport is usable.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Delivery state of one outbox row (see <see cref="NotificationDelivery"/>).</summary>
public enum NotificationDeliveryStatus
{
    /// <summary>Waiting for its (first) send attempt.</summary>
    Pending,

    /// <summary>Delivered to the SMTP server.</summary>
    Sent,

    /// <summary>The last attempt failed; a retry is scheduled at <see cref="NotificationDelivery.NextAttemptAt"/>.</summary>
    Failed,

    /// <summary>All attempts exhausted — kept for the operator to inspect and optionally re-send.</summary>
    DeadLetter,
}

/// <summary>
/// One e-mail notification awaiting (or after) delivery — the notification outbox. The dispatcher writes a row
/// per matching subscription rule instead of sending inline, and a background delivery service sends with
/// retries and exponential backoff; exhausted rows park as <see cref="NotificationDeliveryStatus.DeadLetter"/>.
/// This makes every notification durable, observable (delivery history in the UI) and re-sendable — a dropped
/// SMTP connection or missing configuration can no longer silently swallow an alert.
/// </summary>
public sealed record NotificationDelivery
{
    public required Guid Id { get; init; }

    /// <summary>The event the e-mail announces (for filtering the history).</summary>
    public required NotificationEvent Event { get; init; }

    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }

    /// <summary>The subscription rule that matched (informational; the rule may be edited/deleted later).</summary>
    public Guid? SubscriptionId { get; init; }
    public string RuleName { get; init; } = string.Empty;

    /// <summary>Recipient addresses snapshotted from the rule at dispatch time.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>Rendered e-mail subject/body snapshotted at dispatch time.</summary>
    public required string Subject { get; init; }
    public required string Body { get; init; }

    public NotificationDeliveryStatus Status { get; init; } = NotificationDeliveryStatus.Pending;

    /// <summary>Completed send attempts so far.</summary>
    public int AttemptCount { get; init; }

    /// <summary>When the next attempt is due (Pending/Failed); null once Sent or DeadLetter.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>The last attempt's failure, shown in the delivery history.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; init; }
}
