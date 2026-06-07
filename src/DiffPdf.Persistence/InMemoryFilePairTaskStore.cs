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

    public Task RequeueAsync(Guid taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var t) && t.Status == FilePairTaskStatus.Running)
                _tasks[taskId] = t with
                {
                    Status = FilePairTaskStatus.Queued,
                    LockedBy = null,
                    LockedUntil = null,
                    Version = t.Version + 1,
                };
        }
        return Task.CompletedTask;
    }

    public Task RequeueForRetryAsync(Guid taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var t))
                _tasks[taskId] = t with
                {
                    Status = FilePairTaskStatus.Queued,
                    Result = null,
                    Error = null,
                    AttemptCount = 0,
                    CompletedAt = null,
                    LockedBy = null,
                    LockedUntil = null,
                    Version = t.Version + 1,
                };
        }
        return Task.CompletedTask;
    }

    public Task<int> SkipPendingForJobAsync(Guid jobId, CancellationToken ct = default)
    {
        int skipped = 0;
        lock (_gate)
        {
            foreach (var t in _tasks.Values.Where(t => t.JobId == jobId && t.Status == FilePairTaskStatus.Queued).ToList())
            {
                _tasks[t.Id] = t with
                {
                    Status = FilePairTaskStatus.Skipped,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LockedBy = null,
                    LockedUntil = null,
                    Version = t.Version + 1,
                };
                skipped++;
            }
        }
        return Task.FromResult(skipped);
    }

    public Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueStaleAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var recovered = new List<(Guid, Guid)>();
        lock (_gate)
        {
            foreach (var t in _tasks.Values.Where(t => t.Status == FilePairTaskStatus.Running && t.LockedUntil < now).ToList())
            {
                _tasks[t.Id] = t with { Status = FilePairTaskStatus.Queued, LockedBy = null, LockedUntil = null, Version = t.Version + 1 };
                recovered.Add((t.JobId, t.Id));
            }
        }
        return Task.FromResult<IReadOnlyList<(Guid, Guid)>>(recovered);
    }

    public Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FilePairTask>>(
            _tasks.Values.Where(t => t.JobId == jobId).OrderBy(t => t.RelativePath).ToList());

    public Task<int> CountActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(_tasks.Values.Count(t => t.Status is FilePairTaskStatus.Queued or FilePairTaskStatus.Running));

    public Task<IReadOnlyDictionary<FilePairTaskStatus, int>> CountByStatusForJobsAsync(
        IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<FilePairTaskStatus, int>>(new Dictionary<FilePairTaskStatus, int>());

        var set = jobIds as HashSet<Guid> ?? [.. jobIds];
        var counts = _tasks.Values
            .Where(t => set.Contains(t.JobId))
            .GroupBy(t => t.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult<IReadOnlyDictionary<FilePairTaskStatus, int>>(counts);
    }

    public Task<int> DeleteForJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
            return Task.FromResult(0);

        var set = jobIds as HashSet<Guid> ?? [.. jobIds];
        int removed = 0;
        foreach (var id in _tasks.Values.Where(t => set.Contains(t.JobId)).Select(t => t.Id).ToList())
            if (_tasks.TryRemove(id, out _)) removed++;
        return Task.FromResult(removed);
    }
}
