using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory job store for single-instance / dev use. Mirrors the
/// optimistic-concurrency semantics of the Postgres store so handlers behave
/// identically. Does not survive restart; use the Postgres store in production.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<Guid, ComparisonJob> _jobs = new();
    private readonly object _gate = new();

    public Task<ComparisonJob> CreateAsync(ComparisonJob job, CancellationToken ct = default)
    {
        var created = job with { Version = 1, CreatedAt = job.CreatedAt };
        _jobs[created.Id] = created;
        return Task.FromResult(created);
    }

    public Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

    public Task<IReadOnlyList<ComparisonJob>> ListAsync(JobListQuery query, CancellationToken ct = default)
    {
        var result = Filter(query)
            .OrderByDescending(j => j.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ComparisonJob>>(result);
    }

    public Task<int> CountAsync(JobListQuery query, CancellationToken ct = default) =>
        Task.FromResult(Filter(query).Count());

    private IEnumerable<ComparisonJob> Filter(JobListQuery query)
    {
        IEnumerable<ComparisonJob> q = _jobs.Values;
        if (!string.IsNullOrEmpty(query.BranchKey))
            q = q.Where(j => j.BranchKey == query.BranchKey);
        if (!string.IsNullOrEmpty(query.InstanceKey))
            q = q.Where(j => j.InstanceKey == query.InstanceKey);
        if (query.Status is { } s)
            q = q.Where(j => j.Status == s);
        if (query.TriggerId is { } tid)
            q = q.Where(j => j.TriggerId == tid);
        return q;
    }

    public Task<int> CountActiveByBranchAsync(Guid branchId, CancellationToken ct = default) =>
        Task.FromResult(_jobs.Values.Count(j => j.BranchId == branchId
            && j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused));

    public Task<ComparisonJob?> NextDraftForBranchAsync(Guid branchId, CancellationToken ct = default) =>
        Task.FromResult(_jobs.Values
            .Where(j => j.BranchId == branchId && j.Status == JobStatus.Draft)
            .OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt)
            .FirstOrDefault());

    public Task<IReadOnlyList<ComparisonJob>> ListActiveAndDraftByBranchAsync(Guid branchId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonJob>>(_jobs.Values
            .Where(j => j.BranchId == branchId
                && j.Status is JobStatus.Draft or JobStatus.Queued or JobStatus.Running or JobStatus.Paused)
            .OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt)
            .ToList());

    public Task<IReadOnlyList<ComparisonJob>> ListStaleUnindexedRunningAsync(DateTimeOffset leaseExpiredBefore, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonJob>>(_jobs.Values
            .Where(j => j.Status == JobStatus.Running && j.TotalCount == 0 && j.LockedUntil is { } l && l < leaseExpiredBefore)
            .OrderBy(j => j.LockedUntil)
            .Take(limit)
            .ToList());

    public Task<ComparisonJob?> TryStartAsync(Guid id, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Queued)
                return Task.FromResult<ComparisonJob?>(null);

            var started = job with
            {
                Status = JobStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LockedBy = workerId,
                LockedUntil = DateTimeOffset.UtcNow.Add(lease),
                Version = job.Version + 1,
            };
            _jobs[id] = started;
            return Task.FromResult<ComparisonJob?>(started);
        }
    }

    public Task<ComparisonJob> UpdateProgressAsync(Guid id, int processedCount, int totalCount, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var job = Guard(id, expectedVersion, JobStatus.Running);
            var updated = job with
            {
                ProcessedCount = processedCount,
                TotalCount = totalCount,
                UpdatedAt = DateTimeOffset.UtcNow,
                LockedUntil = DateTimeOffset.UtcNow.AddMinutes(5),
                Version = job.Version + 1,
            };
            _jobs[id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<ComparisonJob> CompleteAsync(Guid id, BatchComparisonReport report, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var job = Guard(id, expectedVersion, JobStatus.Running);
            var completed = job with
            {
                Status = JobStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Report = report,
                ProcessedCount = report.Total,
                TotalCount = report.Total,
                Error = null,
                LockedBy = null,
                LockedUntil = null,
                Version = job.Version + 1,
            };
            _jobs[id] = completed;
            return Task.FromResult(completed);
        }
    }

    public Task<ComparisonJob> FailAsync(Guid id, string error, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var job = Guard(id, expectedVersion, JobStatus.Running, JobStatus.Queued);
            var failed = job with
            {
                Status = JobStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = error,
                LockedBy = null,
                LockedUntil = null,
                Version = job.Version + 1,
            };
            _jobs[id] = failed;
            return Task.FromResult(failed);
        }
    }

    public Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status is not (JobStatus.Draft or JobStatus.Queued or JobStatus.Running or JobStatus.Paused))
                return Task.FromResult<ComparisonJob?>(null);

            var cancelled = job with
            {
                Status = JobStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LockedBy = null,
                LockedUntil = null,
                Version = job.Version + 1,
            };
            _jobs[id] = cancelled;
            return Task.FromResult<ComparisonJob?>(cancelled);
        }
    }

    public Task<ComparisonJob?> EnqueueAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Draft)
                return Task.FromResult<ComparisonJob?>(null);

            var queued = job with
            {
                Status = JobStatus.Queued,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = job.Version + 1,
            };
            _jobs[id] = queued;
            return Task.FromResult<ComparisonJob?>(queued);
        }
    }

    public Task<ComparisonJob?> PauseAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Running)
                return Task.FromResult<ComparisonJob?>(null);

            var paused = job with
            {
                Status = JobStatus.Paused,
                UpdatedAt = DateTimeOffset.UtcNow,
                LockedBy = null,
                LockedUntil = null,
                Version = job.Version + 1,
            };
            _jobs[id] = paused;
            return Task.FromResult<ComparisonJob?>(paused);
        }
    }

    public Task<ComparisonJob?> ResumeAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Paused)
                return Task.FromResult<ComparisonJob?>(null);

            var resumed = job with
            {
                Status = JobStatus.Running,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = job.Version + 1,
            };
            _jobs[id] = resumed;
            return Task.FromResult<ComparisonJob?>(resumed);
        }
    }

    public Task<ComparisonJob?> ReopenAsync(Guid id, int processedCount, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status is not (JobStatus.Completed or JobStatus.Failed))
                return Task.FromResult<ComparisonJob?>(null);

            var reopened = job with
            {
                Status = JobStatus.Running,
                ProcessedCount = processedCount,
                Report = null,
                Error = null,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = null,
                Version = job.Version + 1,
            };
            _jobs[id] = reopened;
            return Task.FromResult<ComparisonJob?>(reopened);
        }
    }

    public Task SetTotalAsync(Guid id, int total, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var job))
                _jobs[id] = job with { TotalCount = total, UpdatedAt = DateTimeOffset.UtcNow };
        }
        return Task.CompletedTask;
    }

    public Task<(int Processed, int Total)> IncrementProcessedAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var job = _jobs[id];
            var updated = job with { ProcessedCount = job.ProcessedCount + 1, UpdatedAt = DateTimeOffset.UtcNow };
            _jobs[id] = updated;
            return Task.FromResult((updated.ProcessedCount, updated.TotalCount));
        }
    }

    public Task<IReadOnlyList<ComparisonJob>> ListPrunableArtifactsAsync(DateTimeOffset completedBefore, int limit, CancellationToken ct = default)
    {
        var result = _jobs.Values
            .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled
                     && j.CompletedAt is { } c && c < completedBefore
                     && j.ArtifactsPrunedAt is null)
            .OrderBy(j => j.CompletedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ComparisonJob>>(result);
    }

    public Task MarkArtifactsPrunedAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var job))
                _jobs[id] = job with { ArtifactsPrunedAt = at };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        var counts = _jobs.Values
            .GroupBy(j => j.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult<IReadOnlyDictionary<JobStatus, int>>(counts);
    }

    public Task<IReadOnlyList<Guid>> ListPrunableRowsAsync(DateTimeOffset completedBefore, int limit, CancellationToken ct = default)
    {
        var ids = _jobs.Values
            .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled
                     && j.CompletedAt is { } c && c < completedBefore
                     && j.ArtifactsPrunedAt is not null)
            .OrderBy(j => j.CompletedAt)
            .Take(limit)
            .Select(j => j.Id)
            .ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    public Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var id in ids)
            if (_jobs.TryRemove(id, out _)) removed++;
        return Task.FromResult(removed);
    }

    private ComparisonJob Guard(Guid id, long expectedVersion, params JobStatus[] allowed)
    {
        if (!_jobs.TryGetValue(id, out var job))
            throw new ConcurrencyConflictException($"Job {id} not found.");
        if (job.Version != expectedVersion)
            throw new ConcurrencyConflictException($"Job {id} version {job.Version} != expected {expectedVersion}.");
        if (!allowed.Contains(job.Status))
            throw new ConcurrencyConflictException($"Job {id} is {job.Status}, expected one of [{string.Join(",", allowed)}].");
        return job;
    }
}
