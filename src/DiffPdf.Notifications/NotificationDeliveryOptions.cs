namespace DiffPdf.Notifications;

/// <summary>Tuning for the notification outbox delivery service (<c>NotificationDelivery</c> config section).</summary>
public sealed class NotificationDeliveryOptions
{
    public const string SectionName = "NotificationDelivery";

    /// <summary>Master switch; when false queued rows stay Pending (nothing is sent).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the leader scans for due rows.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Max rows sent per tick (backpressure for an SMTP burst after an outage).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Send attempts before a row parks as DeadLetter.</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, IntervalSeconds));

    /// <summary>Backoff before the next attempt: 1 min after the first failure, 5 min after the second, then 30 min.</summary>
    public static TimeSpan Backoff(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(30),
    };
}
