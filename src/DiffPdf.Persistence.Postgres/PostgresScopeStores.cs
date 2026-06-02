using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DiffPdf.Persistence.Postgres;

public sealed class PostgresBusinessInstanceStore(DiffPdfDbContext db, EntityMapper mapper) : IBusinessInstanceStore
{
    public async Task<BusinessInstance> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        db.BusinessInstances.Add(new BusinessInstanceEntity { Id = Guid.NewGuid(), Key = key, Name = name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateKeyException($"Business instance '{key}' already exists.");
        }
        return (await GetByKeyAsync(key, ct))!;
    }

    public async Task<BusinessInstance?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var e = await db.BusinessInstances.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<BusinessInstance>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.BusinessInstances.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }
}

public sealed class PostgresProjectStore(DiffPdfDbContext db, EntityMapper mapper) : IProjectStore
{
    public async Task<ComparisonProject> CreateAsync(Guid businessInstanceId, string key, string name, CancellationToken ct = default)
    {
        db.Projects.Add(new ProjectEntity { Id = Guid.NewGuid(), BusinessInstanceId = businessInstanceId, Key = key, Name = name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateKeyException($"Project '{key}' already exists in this business instance.");
        }
        return (await GetByKeyAsync(businessInstanceId, key, ct))!;
    }

    public async Task<ComparisonProject?> GetByKeyAsync(Guid businessInstanceId, string key, CancellationToken ct = default)
    {
        var e = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.BusinessInstanceId == businessInstanceId && x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<ComparisonProject>> ListAsync(Guid businessInstanceId, CancellationToken ct = default)
    {
        var rows = await db.Projects.AsNoTracking()
            .Where(x => x.BusinessInstanceId == businessInstanceId).OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }
}
