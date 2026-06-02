using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

public sealed class InMemoryFilePairTaskStore : IFilePairTaskStore
{
    private readonly ConcurrentDictionary<Guid, FilePairTask> _tasks = new();
    private readonly object _gate = new();

    public Task CreateManyAsync(IReadOnlyList<FilePairTask> tasks, CancellationToken ct = default)
    {
        foreach (var t in tasks) _tasks[t.Id] = t;
        return Task.CompletedTask;
    }

    public Task<FilePairTask?> TryClaimAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var t) || t.Status != FilePairTaskStatus.Queued)
                return Task.FromResult<FilePairTask?>(null);

            var claimed = t with
            {
                Status = FilePairTaskStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                AttemptCount = t.AttemptCount + 1,
                LockedBy = workerId,
                LockedUntil = DateTimeOffset.UtcNow.Add(lease),
                Version = t.Version + 1,
            };
            _tasks[taskId] = claimed;
            return Task.FromResult<FilePairTask?>(claimed);
        }
    }

    public Task CompleteAsync(Guid taskId, FilePairResult result, FilePairTaskStatus status, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var t))
                _tasks[taskId] = t with
                {
                    Status = status,
                    Result = result,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LockedBy = null,
                    LockedUntil = null,
                    Version = t.Version + 1,
                };
        }
        return Task.CompletedTask;
    }

    public Task FailAsync(Guid taskId, string error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var t))
                _tasks[taskId] = t with
                {
                    Status = FilePairTaskStatus.Failed,
                    Error = error,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LockedBy = null,
                    LockedUntil = null,
                    Version = t.Version + 1,
                };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FilePairTask>>(
            _tasks.Values.Where(t => t.JobId == jobId).OrderBy(t => t.RelativePath).ToList());
}
