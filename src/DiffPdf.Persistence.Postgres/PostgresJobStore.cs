using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace DiffPdf.Persistence.Postgres;

/// <summary>PostgreSQL job store — the production source of truth with optimistic concurrency.</summary>
public sealed class PostgresJobStore(NpgsqlDataSource dataSource) : IJobStore
{
    private const string Columns =
        "id, business_instance_id, project_id, status, created_at, updated_at, started_at, completed_at, " +
        "processed_count, total_count, request_json, report_json, error, version, locked_by, locked_until";

    public async Task<ComparisonJob> CreateAsync(ComparisonJob job, CancellationToken ct = default)
    {
        const string sql = """
            insert into jobs (id, business_instance_id, project_id, status, created_at, processed_count, total_count, request_json, version)
            values (@id, @bi, @proj, @status, @createdAt, @processed, @total, @req::jsonb, 1)
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", job.Id);
        cmd.Parameters.AddWithValue("bi", job.BusinessInstanceId);
        cmd.Parameters.AddWithValue("proj", job.ProjectId);
        cmd.Parameters.AddWithValue("status", job.Status.ToString());
        cmd.Parameters.AddWithValue("createdAt", job.CreatedAt);
        cmd.Parameters.AddWithValue("processed", job.ProcessedCount);
        cmd.Parameters.AddWithValue("total", job.TotalCount);
        cmd.Parameters.Add(new NpgsqlParameter("req", NpgsqlDbType.Text) { Value = DiffPdfJson.Serialize(job.Request) });
        await cmd.ExecuteNonQueryAsync(ct);

        return (await GetAsync(job.Id, ct))!;
    }

    public async Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"select {Columns} from jobs where id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await ReadSingleAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<ComparisonJob>> ListAsync(JobListQuery query, CancellationToken ct = default)
    {
        const string sql = $"""
            select {Columns} from jobs
            where (@biKey::text is null or business_instance_id = (select id from business_instances where key = @biKey::text))
              and (@projKey::text is null or project_id in (
                    select p.id from projects p
                    join business_instances bi on bi.id = p.business_instance_id
                    where p.key = @projKey::text and (@biKey::text is null or bi.key = @biKey::text)))
              and (@status::text is null or status = @status::text)
            order by created_at desc
            limit @limit
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("biKey", (object?)query.BusinessInstanceKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("projKey", (object?)query.ProjectKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (object?)query.Status?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", query.Limit);

        var jobs = new List<ComparisonJob>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) jobs.Add(Map(reader));
        return jobs;
    }

    public async Task<ComparisonJob?> TryStartAsync(Guid id, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        const string sql = $"""
            update jobs
            set status = 'Running', started_at = now(), updated_at = now(),
                locked_by = @worker, locked_until = now() + @lease, version = version + 1
            where id = @id and status = 'Queued'
            returning {Columns}
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("worker", workerId);
        cmd.Parameters.AddWithValue("lease", lease);
        return await ReadSingleAsync(cmd, ct);
    }

    public async Task<ComparisonJob> UpdateProgressAsync(Guid id, int processedCount, int totalCount, long expectedVersion, CancellationToken ct = default)
    {
        const string sql = $"""
            update jobs
            set processed_count = @processed, total_count = @total, updated_at = now(),
                locked_until = now() + interval '5 minutes', version = version + 1
            where id = @id and status = 'Running' and version = @version
            returning {Columns}
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("processed", processedCount);
        cmd.Parameters.AddWithValue("total", totalCount);
        cmd.Parameters.AddWithValue("version", expectedVersion);
        return await ReadSingleAsync(cmd, ct)
            ?? throw new ConcurrencyConflictException($"Progress update conflict for job {id} (expected version {expectedVersion}).");
    }

    public async Task<ComparisonJob> CompleteAsync(Guid id, BatchComparisonReport report, long expectedVersion, CancellationToken ct = default)
    {
        const string sql = $"""
            update jobs
            set status = 'Completed', completed_at = now(), updated_at = now(),
                processed_count = @total, total_count = @total,
                report_json = @report::jsonb, error = null,
                locked_by = null, locked_until = null, version = version + 1
            where id = @id and status = 'Running' and version = @version
            returning {Columns}
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("total", report.Total);
        cmd.Parameters.Add(new NpgsqlParameter("report", NpgsqlDbType.Text) { Value = DiffPdfJson.Serialize(report) });
        cmd.Parameters.AddWithValue("version", expectedVersion);
        return await ReadSingleAsync(cmd, ct)
            ?? throw new ConcurrencyConflictException($"Complete conflict for job {id} (expected version {expectedVersion}).");
    }

    public async Task<ComparisonJob> FailAsync(Guid id, string error, long expectedVersion, CancellationToken ct = default)
    {
        const string sql = $"""
            update jobs
            set status = 'Failed', completed_at = now(), updated_at = now(), error = @error,
                locked_by = null, locked_until = null, version = version + 1
            where id = @id and status in ('Queued','Running') and version = @version
            returning {Columns}
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("error", error);
        cmd.Parameters.AddWithValue("version", expectedVersion);
        return await ReadSingleAsync(cmd, ct)
            ?? throw new ConcurrencyConflictException($"Fail conflict for job {id} (expected version {expectedVersion}).");
    }

    private static async Task<ComparisonJob?> ReadSingleAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static ComparisonJob Map(NpgsqlDataReader r)
    {
        var request = DiffPdfJson.Deserialize<BatchComparisonRequest>(r.GetString(10));
        BatchComparisonReport? report = r.IsDBNull(11) ? null : DiffPdfJson.Deserialize<BatchComparisonReport>(r.GetString(11));

        return new ComparisonJob
        {
            Id = r.GetGuid(0),
            BusinessInstanceId = r.GetGuid(1),
            ProjectId = r.GetGuid(2),
            Request = request,
            Status = Enum.Parse<JobStatus>(r.GetString(3)),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
            UpdatedAt = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
            StartedAt = r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            CompletedAt = r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
            ProcessedCount = r.GetInt32(8),
            TotalCount = r.GetInt32(9),
            Report = report,
            Error = r.IsDBNull(12) ? null : r.GetString(12),
            Version = r.GetInt64(13),
            LockedBy = r.IsDBNull(14) ? null : r.GetString(14),
            LockedUntil = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        };
    }
}
