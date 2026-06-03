namespace DiffPdf.Notifications;

/// <summary>Outbound notification configuration, bound from the <c>Notifications</c> appsettings section.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Master switch. When false no notifications are dispatched (default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Optional public base URL of the API, used to build deep links to a job/report.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>SMTP server settings; required only when a subscription uses the <c>smtp</c> channel.</summary>
    public SmtpOptions? Smtp { get; set; }

    /// <summary>Who gets notified about what. Evaluated for every finished batch.</summary>
    public List<NotificationSubscription> Subscriptions { get; set; } = [];
}

/// <summary>SMTP transport settings for the e-mail channel.</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "diffpdf@localhost";
}

/// <summary>
/// One notification rule: which events, optionally scoped to a branch/instance, and where to send them.
/// </summary>
public sealed class NotificationSubscription
{
    /// <summary>Delivery channel: <c>webhook</c> (Slack/Teams/generic JSON POST) or <c>smtp</c> (e-mail).</summary>
    public string Channel { get; set; } = "webhook";

    /// <summary>The webhook URL or the recipient e-mail address, depending on <see cref="Channel"/>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Events this subscription cares about. Empty = none.</summary>
    public List<NotificationEvent> Events { get; set; } = [];

    /// <summary>Optional branch filter (case-insensitive); null/empty = any branch.</summary>
    public string? BranchKey { get; set; }

    /// <summary>Optional instance filter (case-insensitive); null/empty = any instance.</summary>
    public string? InstanceKey { get; set; }
}
