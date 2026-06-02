using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core job store — production source of truth with atomic, version-guarded transitions.</summary>
public sealed class SqlServerJobStore(DiffPdfDbContext db, EntityMapper mapper) : IJobStore
{
    public async Task<ComparisonJob> CreateAsync(ComparisonJob job, CancellationToken ct = default)
    {
        db.Jobs.Add(ToEntity(job));
        await db.SaveChangesAsync(ct);
        return (await GetAsync(job.Id, ct))!;
    }

    public async Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return entity is null ? null : mapper.ToDomain(entity);
    }

    public async Task<IReadOnlyList<ComparisonJob>> ListAsync(JobListQuery query, CancellationToken ct = default)
    {
        string? status = query.Status?.ToString();
        var q =
            from j in db.Jobs.AsNoTracking()
            join bi in db.BusinessInstances.AsNoTracking() on j.BusinessInstanceId equals bi.Id
            join p in db.Projects.AsNoTracking() on j.ProjectId equals p.Id
            where (query.BusinessInstanceKey == null || bi.Key == query.BusinessInstanceKey)
               && (query.ProjectKey == null || p.Key == query.ProjectKey)
               && (status == null || j.Status == status)
            orderby j.CreatedAt descending
            select j;

        var rows = await q.Take(query.Limit).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<ComparisonJob?> TryStartAsync(Guid id, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Queued")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Running")
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.LockedBy, workerId)
                .SetProperty(j => j.LockedUntil, now.Add(lease))
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<ComparisonJob> UpdateProgressAsync(Guid id, int processedCount, int totalCount, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Running" && j.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.ProcessedCount, processedCount)
                .SetProperty(j => j.TotalCount, totalCount)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.LockedUntil, now.AddMinutes(5))
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Progress update conflict for job {id} (expected version {expectedVersion}).")
            : (await GetAsync(id, ct))!;
    }

    public async Task<ComparisonJob> CompleteAsync(Guid id, BatchComparisonReport report, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string reportJson = DiffPdfJson.Serialize(report);
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Running" && j.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Completed")
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.ProcessedCount, report.Total)
                .SetProperty(j => j.TotalCount, report.Total)
                .SetProperty(j => j.ReportJson, reportJson)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Complete conflict for job {id} (expected version {expectedVersion}).")
            : (await GetAsync(id, ct))!;
    }

    public async Task<ComparisonJob> FailAsync(Guid id, string error, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && (j.Status == "Running" || j.Status == "Queued") && j.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Failed")
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Fail conflict for job {id} (expected version {expectedVersion}).")
            : (await GetAsync(id, ct))!;
    }

    public async Task<ComparisonJob?> UpdateRequestAsync(Guid id, BatchComparisonRequest request, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string requestJson = DiffPdfJson.Serialize(request);
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Queued")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.RequestJson, requestJson)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        int rows = await db.Jobs
            .Where(j => j.Id == id && (j.Status == "Completed" || j.Status == "Failed" || j.Status == "Cancelled"))
            .ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && (j.Status == "Queued" || j.Status == "Running" || j.Status == "Paused"))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Cancelled")
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<ComparisonJob?> PauseAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Paused")
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<ComparisonJob?> ResumeAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Paused")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Running")
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<ComparisonJob?> ReopenAsync(Guid id, int processedCount, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && (j.Status == "Completed" || j.Status == "Failed"))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Running")
                .SetProperty(j => j.ProcessedCount, processedCount)
                .SetProperty(j => j.ReportJson, (string?)null)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task SetTotalAsync(Guid id, int total, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Jobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.TotalCount, total)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }

    public async Task<(int Processed, int Total)> IncrementProcessedAsync(Guid id, CancellationToken ct = default)
    {
        // Atomic increment + read in one statement so concurrent file-pair
        // completions never lose a count or miss the finalize trigger.
        await using var conn = new SqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "update jobs set processed_count = processed_count + 1, updated_at = sysdatetimeoffset() output inserted.processed_count, inserted.total_count where id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt32(0), r.GetInt32(1));
    }

    internal static JobEntity ToEntity(ComparisonJob job) => new()
    {
        Id = job.Id,
        BusinessInstanceId = job.BusinessInstanceId,
        ProjectId = job.ProjectId,
        Status = job.Status.ToString(),
        CreatedAt = job.CreatedAt,
        ProcessedCount = job.ProcessedCount,
        TotalCount = job.TotalCount,
        RequestJson = DiffPdfJson.Serialize(job.Request),
        Version = 1,
    };
}
