using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

public sealed class SqlServerBranchStore(DiffPdfDbContext db, EntityMapper mapper) : IBranchStore
{
    public async Task<Branch> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        db.Branches.Add(new BranchEntity { Id = Guid.NewGuid(), Key = key, Name = name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException($"Branch '{key}' already exists.");
        }
        return (await GetByKeyAsync(key, ct))!;
    }

    public async Task<Branch> UpdateAsync(Branch branch, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Branches
            .Where(x => x.Key == branch.Key && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, branch.Name)
                .SetProperty(x => x.Enabled, branch.Enabled)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Branch update conflict for '{branch.Key}' (expected version {expectedVersion}).")
            : (await GetByKeyAsync(branch.Key, ct))!;
    }

    public async Task<Branch?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var e = await db.Branches.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<Branch?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Branches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Branches.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task SetQueuePausedAsync(Guid branchId, bool paused, CancellationToken ct = default)
    {
        await db.Branches.Where(x => x.Id == branchId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.QueuePaused, paused)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task<bool> DeleteByKeyAsync(string key, CancellationToken ct = default)
    {
        int rows = await db.Branches.Where(x => x.Key == key).ExecuteDeleteAsync(ct);
        return rows > 0;
    }
}

public sealed class SqlServerInstanceStore(DiffPdfDbContext db, EntityMapper mapper) : IInstanceStore
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
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException($"Instance '{key}' already exists in this branch.");
        }
        return (await GetByKeyAsync(branchId, key, ct))!;
    }

    public async Task<ComparisonInstance> UpdateAsync(ComparisonInstance instance, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Instances
            .Where(x => x.BranchId == instance.BranchId && x.Key == instance.Key && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, instance.Name)
                .SetProperty(x => x.BasePath, instance.BasePath)
                .SetProperty(x => x.CredentialProfile, instance.CredentialProfile)
                .SetProperty(x => x.Enabled, instance.Enabled)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Instance update conflict for '{instance.Key}' (expected version {expectedVersion}).")
            : (await GetByKeyAsync(instance.BranchId, instance.Key, ct))!;
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

    public async Task<bool> DeleteByKeyAsync(Guid branchId, string key, CancellationToken ct = default)
    {
        int rows = await db.Instances.Where(x => x.BranchId == branchId && x.Key == key).ExecuteDeleteAsync(ct);
        return rows > 0;
    }
}
