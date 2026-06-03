namespace DiffPdf.Notifications;

/// <summary>The kind of batch outcome a notification announces.</summary>
public enum NotificationEvent
{
    /// <summary>Batch completed and (if a gate was configured) passed it.</summary>
    Completed,

    /// <summary>Batch completed but violated its CI gate.</summary>
    GateViolated,

    /// <summary>Batch ended in a failed state.</summary>
    Failed,
}

/// <summary>
/// A channel-agnostic description of a finished batch, ready to be rendered into an
/// e-mail, a Slack/Teams message or a generic webhook payload.
/// </summary>
public sealed record BatchNotification(
    NotificationEvent Event,
    Guid JobId,
    string BranchKey,
    string InstanceKey,
    int Total,
    int Identical,
    int Differing,
    int Errors,
    int FilesWithContentErrors,
    bool Passed,
    IReadOnlyList<string> GateViolations,
    DateTimeOffset OccurredAt)
{
    /// <summary>One-line headline suitable for an e-mail subject or chat title.</summary>
    public string Title => Event switch
    {
        NotificationEvent.Failed => $"diffpdf — batch FAILED: {BranchKey}/{InstanceKey}",
        NotificationEvent.GateViolated => $"diffpdf — gate VIOLATED: {BranchKey}/{InstanceKey}",
        _ => $"diffpdf — batch passed: {BranchKey}/{InstanceKey}",
    };

    /// <summary>Human-readable body with the headline counts and any gate violations.</summary>
    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"Job {JobId}",
                $"Files: {Total} total, {Identical} identical, {Differing} differing, {Errors} error(s), {FilesWithContentErrors} with content errors.",
            };
            if (GateViolations.Count > 0)
            {
                lines.Add("Gate violations:");
                lines.AddRange(GateViolations.Select(v => $"  - {v}"));
            }
            lines.Add($"Finished at {OccurredAt:u}.");
            return string.Join('\n', lines);
        }
    }
}
