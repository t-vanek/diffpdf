namespace DiffPdf.Messaging.Messages;

/// <summary>Index the job's folders into per-file tasks and dispatch them.</summary>
public sealed record IndexBatch(Guid JobId);

/// <summary>Compare a single file pair.</summary>
public sealed record CompareFilePair(Guid JobId, Guid TaskId);

/// <summary>Aggregate the file-pair results into the final report and complete the job.</summary>
public sealed record FinalizeBatch(Guid JobId);

/// <summary>
/// Domain event published when a batch finishes (completed, with or without gate
/// violations). Consumed by the notification handler. Carries the headline report
/// figures so subscribers need not reload the job. <see cref="SourceAutomationId"/>
/// carries the automation whose step launched the batch (null for ad-hoc launches),
/// so event-triggered automations can exclude the launcher from re-triggering itself.
/// </summary>
public sealed record BatchFinished(
    Guid JobId,
    string BranchKey,
    string InstanceKey,
    int Total,
    int Identical,
    int Differing,
    int Errors,
    int FilesWithContentErrors,
    bool Passed,
    string[] GateViolations,
    DateTimeOffset CompletedAt,
    Guid? SourceAutomationId = null);

/// <summary>
/// Domain event published when a batch hard-fails (e.g. indexing could not pair the folders).
/// Consumed by the notification handler to raise a <c>Failed</c> notification.
/// <see cref="SourceAutomationId"/>: see <see cref="BatchFinished.SourceAutomationId"/>.
/// </summary>
public sealed record BatchFailed(
    Guid JobId,
    string BranchKey,
    string InstanceKey,
    string Error,
    DateTimeOffset OccurredAt,
    Guid? SourceAutomationId = null);
