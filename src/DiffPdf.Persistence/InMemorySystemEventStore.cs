using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory system event log for single-instance / dev use. Seq is a process-local counter;
/// does not survive restart (clients simply replay from 0 → the whole in-memory history).
/// </summary>
public sealed class InMemorySystemEventStore : ISystemEventStore
{
    private readonly ConcurrentDictionary<long, SystemEvent> _bySeq = new();
    private long _seq;

    public Task<SystemEvent> AppendAsync(SystemEvent evt, CancellationToken ct = default)
    {
        var stamped = evt with { Seq = Interlocked.Increment(ref _seq) };
        _bySeq[stamped.Seq] = stamped;
        return Task.FromResult(stamped);
    }

    public Task<IReadOnlyList<SystemEvent>> ListSinceAsync(long sinceSeq, int limit, SystemEventSeverity? minSeverity = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SystemEvent>>(_bySeq.Values
            .Where(e => e.Seq > sinceSeq && (minSeverity is null || e.Severity >= minSeverity))
            .OrderBy(e => e.Seq)
            .Take(limit)
            .ToList());

    public Task<IReadOnlyList<SystemEvent>> ListRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SystemEvent>>(_bySeq.Values
            .OrderByDescending(e => e.Seq)
            .Take(limit)
            .ToList());

    public Task<bool> ExistsForJobAsync(string type, Guid jobId, DateTimeOffset after, CancellationToken ct = default) =>
        Task.FromResult(_bySeq.Values.Any(e => e.Type == type && e.JobId == jobId && e.OccurredAt > after));

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var e in _bySeq.Values.Where(e => e.OccurredAt < cutoff).ToList())
            if (_bySeq.TryRemove(e.Seq, out _))
                removed++;
        return Task.FromResult(removed);
    }
}
