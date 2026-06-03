namespace DiffPdf.Messaging.Automation;

/// <summary>
/// Configures the default automation provisioned for an instance the first time it is registered —
/// via scope sync (newly discovered folders) or the create-instance API. A disabled <b>schedule</b>
/// and <b>folder-watch</b> are created as templates an operator can enable later, and an optional
/// one-time <b>initial trigger</b> run is fired. When <see cref="Enabled"/> is false (the default)
/// nothing is provisioned, so existing deployments are unaffected.
/// </summary>
public sealed class DefaultAutomationOptions
{
    public const string SectionName = "DefaultAutomation";

    /// <summary>Master switch. When false, no defaults are provisioned (the provisioner is a no-op).</summary>
    public bool Enabled { get; set; }

    /// <summary>Create a default schedule (disabled) for a newly registered instance.</summary>
    public bool CreateSchedule { get; set; } = true;

    /// <summary>Key of the default schedule (unique per instance; used in REST routes).</summary>
    public string ScheduleKey { get; set; } = "default";

    /// <summary>Cron for the default schedule (standard 5-field, evaluated in UTC). Default: daily 02:00.</summary>
    public string ScheduleCron { get; set; } = "0 2 * * *";

    /// <summary>Create a default folder-watch (disabled) for a newly registered instance.</summary>
    public bool CreateWatch { get; set; } = true;

    /// <summary>Stability window of the default watch (seconds): how long <c>new/</c> must be quiet before it would fire.</summary>
    public int WatchStabilitySeconds { get; set; } = 30;

    /// <summary>
    /// Fire a one-time trigger run right after the instance is provisioned. Best-effort: the launcher's
    /// readiness gate skips it when there is nothing to compare, and a failure never fails the registration.
    /// </summary>
    public bool FireInitialTrigger { get; set; } = true;
}
