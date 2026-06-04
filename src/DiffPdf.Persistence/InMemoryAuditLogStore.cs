using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory audit log for dev/test.</summary>
public sealed class InMemoryAuditLogStore : IAuditLogStore
{
    private readonly ConcurrentBag<AuditEntry> _entries = [];

    public Task AddAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(string? entityType = null, string? entityId = null, int limit = 100, CancellationToken ct = default)
    {
        IEnumerable<AuditEntry> q = _entries;
        if (entityType is not null) q = q.Where(e => e.EntityType == entityType);
        if (entityId is not null) q = q.Where(e => e.EntityId == entityId);
        return Task.FromResult<IReadOnlyList<AuditEntry>>(q.OrderByDescending(e => e.At).Take(limit).ToList());
    }

    public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        // ConcurrentBag has no targeted removal: snapshot the survivors, clear, and re-add (dev/test store).
        var survivors = _entries.Where(e => e.At >= before).ToList();
        int removed = _entries.Count - survivors.Count;
        _entries.Clear();
        foreach (var e in survivors) _entries.Add(e);
        return Task.FromResult(removed);
    }
}
