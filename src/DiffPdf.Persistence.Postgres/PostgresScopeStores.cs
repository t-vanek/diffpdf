using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using Npgsql;

namespace DiffPdf.Persistence.Postgres;

public sealed class PostgresBusinessInstanceStore(NpgsqlDataSource dataSource) : IBusinessInstanceStore
{
    public async Task<BusinessInstance> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        const string sql = "insert into business_instances (id, key, name) values (@id, @key, @name)";
        try
        {
            await using var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateKeyException($"Business instance '{key}' already exists.");
        }
        return (await GetByKeyAsync(key, ct))!;
    }

    public async Task<BusinessInstance?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, key, name, enabled, created_at, updated_at, version from business_instances where key = @key");
        cmd.Parameters.AddWithValue("key", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<IReadOnlyList<BusinessInstance>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, key, name, enabled, created_at, updated_at, version from business_instances order by key");
        var list = new List<BusinessInstance>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    private static BusinessInstance Map(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Key = r.GetString(1),
        Name = r.GetString(2),
        Enabled = r.GetBoolean(3),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
        UpdatedAt = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        Version = r.GetInt64(6),
    };
}

public sealed class PostgresProjectStore(NpgsqlDataSource dataSource) : IProjectStore
{
    public async Task<ComparisonProject> CreateAsync(Guid businessInstanceId, string key, string name, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        const string sql = "insert into projects (id, business_instance_id, key, name) values (@id, @bi, @key, @name)";
        try
        {
            await using var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("bi", businessInstanceId);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateKeyException($"Project '{key}' already exists in this business instance.");
        }
        return (await GetByKeyAsync(businessInstanceId, key, ct))!;
    }

    public async Task<ComparisonProject?> GetByKeyAsync(Guid businessInstanceId, string key, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, business_instance_id, key, name, enabled, created_at, updated_at, version from projects where business_instance_id = @bi and key = @key");
        cmd.Parameters.AddWithValue("bi", businessInstanceId);
        cmd.Parameters.AddWithValue("key", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<IReadOnlyList<ComparisonProject>> ListAsync(Guid businessInstanceId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, business_instance_id, key, name, enabled, created_at, updated_at, version from projects where business_instance_id = @bi order by key");
        cmd.Parameters.AddWithValue("bi", businessInstanceId);
        var list = new List<ComparisonProject>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    private static ComparisonProject Map(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        BusinessInstanceId = r.GetGuid(1),
        Key = r.GetString(2),
        Name = r.GetString(3),
        Enabled = r.GetBoolean(4),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(5),
        UpdatedAt = r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        Version = r.GetInt64(7),
    };
}
