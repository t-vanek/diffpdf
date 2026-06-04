using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) append-only audit log.</summary>
public sealed class SqlServerAuditLogStore(DiffPdfDbContext db, EntityMapper mapper) : IAuditLogStore
{
    public async Task AddAsync(AuditEntry entry, CancellationToken ct = default)
    {
        db.AuditLog.Add(ToEntity(entry));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(string? entityType = null, string? entityId = null, int limit = 100, CancellationToken ct = default)
    {
        var q = db.AuditLog.AsNoTracking();
        if (entityType is not null) q = q.Where(x => x.EntityType == entityType);
        if (entityId is not null) q = q.Where(x => x.EntityId == entityId);
        var rows = await q.OrderByDescending(x => x.At).Take(limit).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    private static AuditLogEntity ToEntity(AuditEntry a) => new()
    {
        Id = a.Id,
        At = a.At,
        Actor = a.Actor,
        Source = a.Source,
        Action = a.Action,
        EntityType = a.EntityType,
        EntityId = a.EntityId,
        Detail = a.Detail,
    };
}
