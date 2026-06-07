using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.Postgres;

/// <summary>EF Core job store — production source of truth with atomic, version-guarded transitions.</summary>
public sealed class PostgresJobStore(DiffPdfDbContext db, EntityMapper mapper) : IJobStore
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
        var rows = await FilteredQuery(query)
            .OrderByDescending(j => j.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public Task<int> CountAsync(JobListQuery query, CancellationToken ct = default) =>
        FilteredQuery(query).CountAsync(ct);

    public async Task<IReadOnlyList<JobListItem>> ListSummariesAsync(JobListQuery query, CancellationToken ct = default)
    {
        string? status = query.Status?.ToString();
        var rows = await (
            from j in db.Jobs.AsNoTracking()
            join br in db.Branches.AsNoTracking() on j.BranchId equals br.Id
            join inst in db.Instances.AsNoTracking() on j.InstanceId equals inst.Id
            where (query.BranchKey == null || br.Key == query.BranchKey)
               && (query.InstanceKey == null || inst.Key == query.InstanceKey)
               && (status == null || j.Status == status)
               && (query.TriggerId == null || j.TriggerId == query.TriggerId)
            orderby j.CreatedAt descending
            select new
            {
                j.Id, BranchKey = br.Key, InstanceKey = inst.Key, j.Status,
                j.ProcessedCount, j.TotalCount, j.CreatedAt, j.CompletedAt, j.Error,
                j.DifferingCount, j.ErrorCount, j.GatePassed,
            })
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit)
            .ToListAsync(ct);

        return rows.Select(r => new JobListItem
        {
            Id = r.Id, BranchKey = r.BranchKey, InstanceKey = r.InstanceKey,
            Status = Enum.TryParse<JobStatus>(r.Status, out var s) ? s : JobStatus.Queued,
            ProcessedCount = r.ProcessedCount, TotalCount = r.TotalCount,
            CreatedAt = r.CreatedAt, CompletedAt = r.CompletedAt, Error = r.Error,
            Differing = r.DifferingCount, Errors = r.ErrorCount, GatePassed = r.GatePassed,
        }).ToList();
    }

    public async Task<int> BackfillVerdictsAsync(int max, CancellationToken ct = default)
    {
        var rows = await db.Jobs
            .Where(j => j.Status == "Completed" && j.ReportJson != null && j.DifferingCount == null)
            .OrderBy(j => j.CreatedAt).Take(max).ToListAsync(ct);
        foreach (var e in rows)
        {
            if (DiffPdfJson.Deserialize<BatchComparisonReport>(e.ReportJson!) is not { } report) continue;
            e.DifferingCount = report.Differing;
            e.ErrorCount = report.Errors;
            e.GatePassed = report.Passed;
        }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private IQueryable<JobEntity> FilteredQuery(JobListQuery query)
    {
        string? status = query.Status?.ToString();
        return
            from j in db.Jobs.AsNoTracking()
            join br in db.Branches.AsNoTracking() on j.BranchId equals br.Id
            join inst in db.Instances.AsNoTracking() on j.InstanceId equals inst.Id
            where (query.BranchKey == null || br.Key == query.BranchKey)
               && (query.InstanceKey == null || inst.Key == query.InstanceKey)
               && (status == null || j.Status == status)
               && (query.TriggerId == null || j.TriggerId == query.TriggerId)
            select j;
    }

    public async Task<IReadOnlyList<ComparisonJob>> ListActiveAndDraftByBranchAsync(Guid branchId, CancellationToken ct = default)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.BranchId == branchId
                && (j.Status == "Draft" || j.Status == "Queued" || j.Status == "Running" || j.Status == "Paused"))
            .OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ComparisonJob>> ListAllActiveAndDraftAsync(CancellationToken ct = default)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == "Draft" || j.Status == "Queued" || j.Status == "Running" || j.Status == "Paused")
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ComparisonJob>> ListStaleUnindexedRunningAsync(DateTimeOffset leaseExpiredBefore, int limit, CancellationToken ct = default)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == "Running" && j.TotalCount == 0 && j.LockedUntil != null && j.LockedUntil < leaseExpiredBefore)
            .OrderBy(j => j.LockedUntil)
            .Take(limit)
            .ToListAsync(ct);
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
                .SetProperty(j => j.DifferingCount, (int?)report.Differing)
                .SetProperty(j => j.ErrorCount, (int?)report.Errors)
                .SetProperty(j => j.GatePassed, (bool?)report.Passed)
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

    public async Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && (j.Status == "Draft" || j.Status == "Queued" || j.Status == "Running" || j.Status == "Paused"))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Cancelled")
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Version, j => j.Version + 1), ct);

        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<ComparisonJob?> EnqueueAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.Jobs
            .Where(j => j.Id == id && j.Status == "Draft")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Queued")
                .SetProperty(j => j.UpdatedAt, now)
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
        await using var conn = new Npgsql.NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "update jobs set processed_count = processed_count + 1, updated_at = now() where id = @id returning processed_count, total_count", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt32(0), r.GetInt32(1));
    }

    public async Task<IReadOnlyList<ComparisonJob>> ListPrunableArtifactsAsync(DateTimeOffset completedBefore, int limit, CancellationToken ct = default)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => (j.Status == "Completed" || j.Status == "Failed" || j.Status == "Cancelled")
                     && j.CompletedAt != null && j.CompletedAt < completedBefore
                     && j.ArtifactsPrunedAt == null)
            .OrderBy(j => j.CompletedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task MarkArtifactsPrunedAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await db.Jobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ArtifactsPrunedAt, at), ct);
    }

    public async Task<IReadOnlyList<Guid>> ListPrunableRowsAsync(DateTimeOffset completedBefore, int limit, CancellationToken ct = default) =>
        await db.Jobs.AsNoTracking()
            .Where(j => (j.Status == "Completed" || j.Status == "Failed" || j.Status == "Cancelled")
                     && j.CompletedAt != null && j.CompletedAt < completedBefore
                     && j.ArtifactsPrunedAt != null)
            .OrderBy(j => j.CompletedAt)
            .Take(limit)
            .Select(j => j.Id)
            .ToListAsync(ct);

    public async Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        int removed = 0;
        foreach (var chunk in ids.Chunk(1000))
            removed += await db.Jobs.Where(j => chunk.Contains(j.Id)).ExecuteDeleteAsync(ct);
        return removed;
    }

    public async Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        var rows = await db.Jobs.AsNoTracking()
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var result = new Dictionary<JobStatus, int>();
        foreach (var r in rows)
            if (Enum.TryParse<JobStatus>(r.Status, out var s))
                result[s] = r.Count;
        return result;
    }

    internal static JobEntity ToEntity(ComparisonJob job) => new()
    {
        Id = job.Id,
        BranchId = job.BranchId,
        InstanceId = job.InstanceId,
        Status = job.Status.ToString(),
        CreatedAt = job.CreatedAt,
        ProcessedCount = job.ProcessedCount,
        TotalCount = job.TotalCount,
        RequestJson = DiffPdfJson.Serialize(job.Request),
        TriggerId = job.TriggerId,
        Source = job.Source.ToString(),
        Priority = job.Priority,
        Version = 1,
    };
}
