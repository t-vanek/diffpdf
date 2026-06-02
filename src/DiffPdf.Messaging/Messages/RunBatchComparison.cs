namespace DiffPdf.Messaging.Messages;

/// <summary>
/// Command to run a batch comparison job. The payload carries scope only for
/// observability; the handler always loads the authoritative job from the store.
/// </summary>
public sealed record RunBatchComparison(Guid JobId, string BranchKey, string InstanceKey);
