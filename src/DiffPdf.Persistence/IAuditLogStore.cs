using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Append-only audit log for trigger / job lifecycle actions.</summary>
public interface IAuditLogStore
{
    Task AddAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>Audit entries (newest first), optionally filtered to one entity.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(string? entityType = null, string? entityId = null, int limit = 100, CancellationToken ct = default);

    /// <summary>Bulk-deletes audit entries recorded before the cutoff (DB-row retention). Returns rows removed.</summary>
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken ct = default);
}
