using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Persistence for per-file-pair tasks (phase 5).</summary>
public interface IFilePairTaskStore
{
    Task CreateManyAsync(IReadOnlyList<FilePairTask> tasks, CancellationToken ct = default);

    /// <summary>Atomically claims a Queued task (Queued → Running). Null if it could not be claimed.</summary>
    Task<FilePairTask?> TryClaimAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken ct = default);

    Task CompleteAsync(Guid taskId, FilePairResult result, FilePairTaskStatus status, CancellationToken ct = default);

    Task FailAsync(Guid taskId, string error, CancellationToken ct = default);

    /// <summary>Returns a claimed task to the queue (Running → Queued) for another attempt.</summary>
    Task RequeueAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>Resets a task (any state → Queued, clears result/error/attempts) for a manual retry.</summary>
    Task RequeueForRetryAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Requeues tasks whose lease expired (crashed worker) and returns their
    /// (jobId, taskId) so they can be re-dispatched. Enables resume.
    /// </summary>
    Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueStaleAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Deletes all tasks of a job (used when the job itself is deleted).</summary>
    Task DeleteByJobAsync(Guid jobId, CancellationToken ct = default);
}
