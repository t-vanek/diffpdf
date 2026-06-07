using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

public sealed class SqlServerFilePairTaskStore(DiffPdfDbContext db, EntityMapper mapper) : IFilePairTaskStore
{
    public async Task CreateManyAsync(IReadOnlyList<FilePairTask> tasks, CancellationToken ct = default)
    {
        db.FilePairTasks.AddRange(tasks.Select(ToEntity));
        await db.SaveChangesAsync(ct);
    }

    public async Task<FilePairTask?> TryClaimAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows = await db.FilePairTasks
            .Where(t => t.Id == taskId && t.Status == "Queued")
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Running")
                .SetProperty(t => t.StartedAt, now)
                .SetProperty(t => t.AttemptCount, t => t.AttemptCount + 1)
                .SetProperty(t => t.LockedBy, workerId)
                .SetProperty(t => t.LockedUntil, now.Add(lease))
                .SetProperty(t => t.Version, t => t.Version + 1), ct);

        if (rows == 0) return null;
        var entity = await db.FilePairTasks.AsNoTracking().FirstAsync(t => t.Id == taskId, ct);
        return mapper.ToDomain(entity);
    }

    public async Task CompleteAsync(Guid taskId, FilePairResult result, FilePairTaskStatus status, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string resultJson = DiffPdfJson.Serialize(result);
        await db.FilePairTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, status.ToString())
                .SetProperty(t => t.ResultJson, resultJson)
                .SetProperty(t => t.CompletedAt, now)
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
    }

    public async Task FailAsync(Guid taskId, string error, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.FilePairTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Failed")
                .SetProperty(t => t.Error, error)
                .SetProperty(t => t.CompletedAt, now)
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
    }

    public async Task RequeueAsync(Guid taskId, CancellationToken ct = default)
    {
        await db.FilePairTasks
            .Where(t => t.Id == taskId && t.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Queued")
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
    }

    public Task<int> SkipPendingForJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return db.FilePairTasks
            .Where(t => t.JobId == jobId && t.Status == "Queued")
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Skipped")
                .SetProperty(t => t.CompletedAt, now)
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
    }

    public Task<int> SkipPendingForTerminalJobsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return db.FilePairTasks
            .Where(t => t.Status == "Queued"
                && db.Jobs.Any(j => j.Id == t.JobId
                    && (j.Status == "Cancelled" || j.Status == "Failed" || j.Status == "Completed")))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Skipped")
                .SetProperty(t => t.CompletedAt, now)
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
    }

    public async Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueStaleAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = await db.FilePairTasks.AsNoTracking()
            .Where(t => t.Status == "Running" && t.LockedUntil < now)
            .Select(t => new { t.Id, t.JobId })
            .ToListAsync(ct);

        if (stale.Count == 0) return [];

        await db.FilePairTasks
            .Where(t => t.Status == "Running" && t.LockedUntil < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Queued")
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);

        return stale.Select(x => (x.JobId, x.Id)).ToList();
    }

    public async Task RequeueForRetryAsync(Guid taskId, CancellationToken ct = default)
    {
        await db.FilePairTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Queued")
                .SetProperty(t => t.ResultJson, (string?)null)
                .SetProperty(t => t.Error, (string?)null)
                .SetProperty(t => t.AttemptCount, 0)
                .SetProperty(t => t.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.LockedBy, (string?)null)
                .SetProperty(t => t.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
    }

    public async Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var rows = await db.FilePairTasks.AsNoTracking()
            .Where(t => t.JobId == jobId).OrderBy(t => t.RelativePath).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<int> CountActiveAsync(CancellationToken ct = default) =>
        await db.FilePairTasks.AsNoTracking().CountAsync(t => t.Status == "Queued" || t.Status == "Running", ct);

    public async Task<IReadOnlyDictionary<FilePairTaskStatus, int>> CountByStatusForJobsAsync(
        IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default)
    {
        var result = new Dictionary<FilePairTaskStatus, int>();
        if (jobIds.Count == 0) return result;

        // Chunk the IN-clause so a scope with very many jobs stays under the SQL parameter limit.
        foreach (var chunk in jobIds.Chunk(1000))
        {
            var rows = await db.FilePairTasks.AsNoTracking()
                .Where(t => chunk.Contains(t.JobId))
                .GroupBy(t => t.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var r in rows)
                if (Enum.TryParse<FilePairTaskStatus>(r.Status, out var s))
                    result[s] = result.GetValueOrDefault(s) + r.Count;
        }
        return result;
    }

    public async Task<int> DeleteForJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return 0;
        int removed = 0;
        foreach (var chunk in jobIds.Chunk(1000))
            removed += await db.FilePairTasks.Where(t => chunk.Contains(t.JobId)).ExecuteDeleteAsync(ct);
        return removed;
    }

    private static FilePairTaskEntity ToEntity(FilePairTask t) => new()
    {
        Id = t.Id,
        JobId = t.JobId,
        RelativePath = t.RelativePath,
        OldFilePath = t.OldFilePath,
        NewFilePath = t.NewFilePath,
        Status = t.Status.ToString(),
        AttemptCount = t.AttemptCount,
        CreatedAt = t.CreatedAt,
        Version = 1,
    };
}
