namespace DiffPdf.Messaging.Scheduling;

/// <summary>Recurring batch schedules, bound from the <c>Schedules</c> appsettings section.</summary>
public sealed class ScheduleOptions
{
    public const string SectionName = "Schedules";

    /// <summary>Master switch. When false the scheduler does not run (default).</summary>
    public bool Enabled { get; set; }

    /// <summary>The recurring batch definitions.</summary>
    public List<ScheduledBatch> Jobs { get; set; } = [];
}

/// <summary>One recurring batch: run the given branch/instance on a cron cadence (UTC).</summary>
public sealed class ScheduledBatch
{
    public string BranchKey { get; set; } = "";
    public string InstanceKey { get; set; } = "";

    /// <summary>Standard 5-field cron expression evaluated in UTC, e.g. <c>0 2 * * *</c> (daily 02:00).</summary>
    public string Cron { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
