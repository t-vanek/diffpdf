namespace DiffPdf.Notifications;

/// <summary>
/// Notification transport configuration, bound from the <c>Notifications</c> appsettings
/// section. The subscriptions themselves are managed at runtime via the API and persisted
/// in the store — this only holds the e-mail transport and the public base URL.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Optional public base URL of the API, used to build deep links to a job/report.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>SMTP server settings; required only when a subscription uses the <c>smtp</c> channel.</summary>
    public SmtpOptions? Smtp { get; set; }

    /// <summary>A deep link to the job resource (status + report) when <see cref="BaseUrl"/> is set; null otherwise.</summary>
    public string? JobLink(Guid jobId) =>
        string.IsNullOrWhiteSpace(BaseUrl) ? null : $"{BaseUrl.TrimEnd('/')}/api/v1/jobs/{jobId}";
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
