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
/// figures so subscribers need not reload the job.
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
    DateTimeOffset CompletedAt);
