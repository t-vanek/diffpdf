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

    /// <summary>A Running comparison job stopped making progress (stalled) past the watchdog's stall window.</summary>
    JobStalled,

    /// <summary>A batch completed (gate passed, or no gate) but some file pairs could not be compared — they
    /// errored, e.g. after exhausting their retries or an unreadable PDF. Escalated so the failures are not
    /// announced silently under a plain <see cref="Completed"/>.</summary>
    CompletedWithErrors,
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
