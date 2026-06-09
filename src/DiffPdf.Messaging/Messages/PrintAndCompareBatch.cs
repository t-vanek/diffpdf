namespace DiffPdf.Messaging.Messages;

/// <summary>
/// Command to print a fresh sample batch on the D3Soft Tisk SOA, fetch it together with the previous
/// batch into the instance's <c>old/</c>/<c>new/</c> folders, and launch a normal DiffPdf comparison.
/// The <see cref="OperationId"/> lets the UI follow the print → fetch → launch phases before the
/// comparison job exists (the handler reports progress against it via <c>IPrintOperationStore</c>).
/// </summary>
public sealed record PrintAndCompareBatch(Guid OperationId, string BranchKey, string InstanceKey);
