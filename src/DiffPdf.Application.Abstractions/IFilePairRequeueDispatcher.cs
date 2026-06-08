namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Re-enqueues a single file pair for (re)processing. Abstracts the messaging transport (Wolverine) away from
/// the application layer, so the job retry path can requeue failed pairs without depending on DiffPdf.Messaging.
/// </summary>
public interface IFilePairRequeueDispatcher
{
    /// <summary>Publishes a compare command for the given job/file-pair task so a worker re-runs it.</summary>
    Task RequeueAsync(Guid jobId, Guid taskId, CancellationToken ct = default);
}
