using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) per-scope configuration store (triggers + comparers, one row per scope).</summary>
public sealed class SqlServerScopeConfigurationStore(DiffPdfDbContext db, EntityMapper mapper) : IScopeConfigurationStore
{
    private const string Global = nameof(ConfigScopeLevel.Global);
    private const string Branch = nameof(ConfigScopeLevel.Branch);
    private const string Instance = nameof(ConfigScopeLevel.Instance);

    public async Task<ScopeConfiguration> CreateAsync(ScopeConfiguration config, CancellationToken ct = default)
    {
        db.ScopeConfigurations.Add(ToEntity(config));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException("A configuration for this scope already exists.");
        }
        return (await GetByIdAsync(config.Id, ct))!;
    }

    public Task<ScopeConfiguration?> GetGlobalAsync(CancellationToken ct = default) =>
        FirstOrNullAsync(x => x.Level == Global, ct);

    public Task<ScopeConfiguration?> GetByBranchAsync(Guid branchId, CancellationToken ct = default) =>
        FirstOrNullAsync(x => x.Level == Branch && x.BranchId == branchId, ct);

    public Task<ScopeConfiguration?> GetByInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        FirstOrNullAsync(x => x.Level == Instance && x.InstanceId == instanceId, ct);

    public async Task<ScopeConfiguration> UpdateAsync(ScopeConfiguration config, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string triggerJson = DiffPdfJson.Serialize(config.TriggerConfig);
        string comparerJson = DiffPdfJson.Serialize(config.ComparisonOptions);

        int rows = await db.ScopeConfigurations
            .Where(x => x.Id == config.Id && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.TriggerSource, config.TriggerSource.ToString())
                .SetProperty(x => x.TriggerConfigJson, triggerJson)
                .SetProperty(x => x.ComparerSource, config.ComparerSource.ToString())
                .SetProperty(x => x.ComparisonOptionsJson, comparerJson)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Scope configuration update conflict for {config.Id} (expected version {expectedVersion}).")
            : (await GetByIdAsync(config.Id, ct))!;
    }

    public async Task<bool> DeleteByBranchAsync(Guid branchId, CancellationToken ct = default) =>
        await db.ScopeConfigurations.Where(x => x.Level == Branch && x.BranchId == branchId).ExecuteDeleteAsync(ct) > 0;

    public async Task<bool> DeleteByInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        await db.ScopeConfigurations.Where(x => x.Level == Instance && x.InstanceId == instanceId).ExecuteDeleteAsync(ct) > 0;

    private async Task<ScopeConfiguration?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var e = await db.ScopeConfigurations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    private async Task<ScopeConfiguration?> FirstOrNullAsync(
        System.Linq.Expressions.Expression<Func<ScopeConfigurationEntity, bool>> predicate, CancellationToken ct)
    {
        var e = await db.ScopeConfigurations.AsNoTracking().FirstOrDefaultAsync(predicate, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    internal static ScopeConfigurationEntity ToEntity(ScopeConfiguration c) => new()
    {
        Id = c.Id,
        Level = c.Level.ToString(),
        BranchId = c.BranchId,
        InstanceId = c.InstanceId,
        TriggerSource = c.TriggerSource.ToString(),
        TriggerConfigJson = DiffPdfJson.Serialize(c.TriggerConfig),
        ComparerSource = c.ComparerSource.ToString(),
        ComparisonOptionsJson = DiffPdfJson.Serialize(c.ComparisonOptions),
        CreatedAt = c.CreatedAt,
        Version = 1,
    };
}
