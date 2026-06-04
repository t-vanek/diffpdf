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
}
