using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Append-only audit log for trigger / job lifecycle actions.</summary>
public interface IAuditLogStore
{
    Task AddAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>Audit entries (newest first), optionally filtered to one entity.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(string? entityType = null, string? entityId = null, int limit = 100, CancellationToken ct = default);
}
