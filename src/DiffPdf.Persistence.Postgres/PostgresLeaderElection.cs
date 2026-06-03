using Npgsql;

namespace DiffPdf.Persistence.Postgres;

/// <summary>
/// PostgreSQL leader lease — a single atomic upsert per call on the one-row-per-role
/// <c>automation_leader</c> table. Acquires when free/expired, renews when already held, and reports
/// no leadership otherwise. Opens its own short-lived connection (no scoped DbContext), so it is a
/// singleton safe to inject directly into the background pollers.
/// </summary>
public sealed class PostgresLeaderElection(string connectionString) : ILeaderElection
{
    private const string Sql = """
        insert into automation_leader (role, owner, acquired_at, renewed_at, expires_at)
        values (@role, @owner, now(), now(), now() + make_interval(secs => @ttl))
        on conflict (role) do update
            set owner = @owner, renewed_at = now(), expires_at = now() + make_interval(secs => @ttl)
            where automation_leader.owner = @owner or automation_leader.expires_at < now()
        returning (owner = @owner);
        """;

    private const string ReadSql = """
        select owner, acquired_at, renewed_at, expires_at from automation_leader where role = @role;
        """;

    public async Task<bool> TryAcquireAsync(string role, string owner, TimeSpan lease, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("ttl", lease.TotalSeconds);

        // The DO UPDATE's WHERE excludes a valid lease held by someone else, so that case
        // upserts nothing and RETURNING yields no row (null) — i.e. not the leader.
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    public async Task<LeaderLease?> GetAsync(string role, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ReadSql, conn);
        cmd.Parameters.AddWithValue("role", role);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new LeaderLease(
            reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }
}
