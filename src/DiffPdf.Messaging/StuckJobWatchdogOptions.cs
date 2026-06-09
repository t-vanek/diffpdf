namespace DiffPdf.Messaging;

/// <summary>Options for the stuck-job watchdog — the periodic comparison-phase stall detector.</summary>
public sealed class StuckJobWatchdogOptions
{
    public const string SectionName = "StuckJobWatchdog";

    /// <summary>How often the watchdog scans Running jobs.</summary>
    public int IntervalSeconds { get; set; } = 60;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>
    /// A Running job with no pair completion for this long is reported as stalled. MUST stay above the per-pair
    /// lease (<c>WorkerOptions.FilePairLease</c>, ~12 min) so a single legitimately-slow pair never trips it.
    /// </summary>
    public int StallThresholdMinutes { get; set; } = 30;

    public TimeSpan StallThreshold => TimeSpan.FromMinutes(StallThresholdMinutes);

    /// <summary>Master switch; when false the watchdog service idles (no scan, no notifications, no metric feed).</summary>
    public bool Enabled { get; set; } = true;
}
