using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DiffPdf.Persistence.Postgres;

public sealed class PostgresBranchStore(DiffPdfDbContext db, EntityMapper mapper) : IBranchStore
{
    public async Task<Branch> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        db.Branches.Add(new BranchEntity { Id = Guid.NewGuid(), Key = key, Name = name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateKeyException($"Branch '{key}' already exists.");
        }
        return (await GetByKeyAsync(key, ct))!;
    }

    public async Task<Branch?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var e = await db.Branches.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Branches.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }
}

public sealed class PostgresInstanceStore(DiffPdfDbContext db, EntityMapper mapper) : IInstanceStore
{
    public async Task<ComparisonInstance> CreateAsync(
        Guid branchId, string key, string name, string basePath, string? credentialProfile, CancellationToken ct = default)
    {
        db.Instances.Add(new InstanceEntity
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            Key = key,
            Name = name,
            BasePath = basePath,
            CredentialProfile = credentialProfile,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateKeyException($"Instance '{key}' already exists in this branch.");
        }
        return (await GetByKeyAsync(branchId, key, ct))!;
    }

    public async Task<ComparisonInstance?> GetByKeyAsync(Guid branchId, string key, CancellationToken ct = default)
    {
        var e = await db.Instances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.BranchId == branchId && x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<ComparisonInstance>> ListAsync(Guid branchId, CancellationToken ct = default)
    {
        var rows = await db.Instances.AsNoTracking()
            .Where(x => x.BranchId == branchId).OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }
}
