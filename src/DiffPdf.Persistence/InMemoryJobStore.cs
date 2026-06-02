using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory job store for the single-instance MVP. Swap for a
/// SQLite/PostgreSQL-backed store to survive restarts and scale out.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<Guid, ComparisonJob> _jobs = new();

    public Task<ComparisonJob> CreateAsync(BatchComparisonRequest request, CancellationToken ct = default)
    {
        var job = new ComparisonJob { Id = Guid.NewGuid(), Request = request };
        _jobs[job.Id] = job;
        return Task.FromResult(job);
    }

    public Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

    public Task<IReadOnlyList<ComparisonJob>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonJob>>(
            _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList());

    public Task UpdateAsync(ComparisonJob job, CancellationToken ct = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }
}
