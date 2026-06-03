namespace DiffPdf.Messaging.Retention;

/// <summary>
/// Artifact-retention configuration, bound from the <c>Retention</c> appsettings section. Only
/// the on-disk artifacts (report folders) of old finished jobs are pruned; DB rows and run
/// history are kept. Disabled by default.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Master switch. When false nothing is pruned (default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Prune the report folder of finished jobs completed more than this many days ago.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often the retention pass runs.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Max jobs pruned per pass (bounds load on large backlogs).</summary>
    public int MaxPerTick { get; set; } = 200;

    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, IntervalHours));
    public TimeSpan MaxAge => TimeSpan.FromDays(Math.Max(1, RetentionDays));
}
