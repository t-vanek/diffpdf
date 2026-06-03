using Microsoft.Data.SqlClient;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>
/// SQL Server leader lease — an atomic <c>MERGE</c> (with HOLDLOCK) per call on the one-row-per-role
/// <c>automation_leader</c> table, then a read-back of whether this owner holds a still-valid lease.
/// Acquires when free/expired, renews when already held, reports no leadership otherwise. Opens its
/// own short-lived connection (no scoped DbContext), so it is a singleton safe to inject into the
/// background pollers.
/// </summary>
public sealed class SqlServerLeaderElection(string connectionString) : ILeaderElection
{
    private const string Sql = """
        merge automation_leader with (holdlock) as t
        using (select @role as role) as s on t.role = s.role
        when matched and (t.owner = @owner or t.expires_at < sysdatetimeoffset()) then
            update set owner = @owner, renewed_at = sysdatetimeoffset(), expires_at = dateadd(second, @ttl, sysdatetimeoffset())
        when not matched then
            insert (role, owner, acquired_at, renewed_at, expires_at)
            values (@role, @owner, sysdatetimeoffset(), sysdatetimeoffset(), dateadd(second, @ttl, sysdatetimeoffset()));
        select case when exists (
            select 1 from automation_leader
            where role = @role and owner = @owner and expires_at >= sysdatetimeoffset()
        ) then 1 else 0 end;
        """;

    public async Task<bool> TryAcquireAsync(string role, string owner, TimeSpan lease, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@owner", owner);
        cmd.Parameters.AddWithValue("@ttl", (int)lease.TotalSeconds);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i && i == 1;
    }
}
