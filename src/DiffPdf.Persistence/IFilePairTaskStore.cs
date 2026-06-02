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

    Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default);
}
