namespace DiffPdf.Messaging.Messages;

/// <summary>Index the job's folders into per-file tasks and dispatch them.</summary>
public sealed record IndexBatch(Guid JobId);

/// <summary>Compare a single file pair.</summary>
public sealed record CompareFilePair(Guid JobId, Guid TaskId);

/// <summary>Aggregate the file-pair results into the final report and complete the job.</summary>
public sealed record FinalizeBatch(Guid JobId);
