using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>
/// A channel-agnostic alert that a Running comparison job appears stalled — it finished indexing but has not
/// completed a file pair within the watchdog's stall window. Emitted by the stuck-job watchdog (notify-only:
/// the job is not failed or cancelled, only surfaced for an operator).
/// </summary>
public sealed record JobStalledNotification(
    Guid JobId,
    string? BranchKey,
    string? InstanceKey,
    int ProcessedCount,
    int TotalCount,
    TimeSpan StalledFor,
    DateTimeOffset LastProgressAt,
    DateTimeOffset OccurredAt) : INotification
{
    public NotificationEvent Event => NotificationEvent.JobStalled;

    public string Title => $"diffpdf — job stalled: {Scope}";

    public string Summary => string.Join('\n',
        $"Comparison job {JobId} has not made progress for {StalledFor.TotalMinutes:0} min.",
        $"scope: {Scope}",
        $"progress: {ProcessedCount}/{TotalCount} pairs; last progress at {LastProgressAt:u}.",
        $"At {OccurredAt:u}.");

    public object Detail => this;

    private string Scope =>
        string.IsNullOrEmpty(BranchKey) ? "global"
        : string.IsNullOrEmpty(InstanceKey) ? BranchKey!
        : $"{BranchKey}/{InstanceKey}";
}
