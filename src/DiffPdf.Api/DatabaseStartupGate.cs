using Microsoft.Data.SqlClient;
using Npgsql;
using Serilog;

namespace DiffPdf.Api;

/// <summary>
/// Pre-startup database gate. Wolverine (and the EF stores) require the application database to exist and
/// be reachable when the host starts — Wolverine provisions its inbox/outbox in <c>StartAsync</c> and cannot
/// tolerate a missing database. This gate runs <b>before</b> <c>app.Run()</c>: it waits (alive, logging,
/// with capped exponential backoff) until the database <i>server</i> is reachable, then creates the
/// application database if it does not yet exist. Schema/tables are then created by the EF Core migrator and
/// Wolverine's own provisioning once the host starts. It never throws, so a transient outage makes the
/// process wait rather than crash-loop.
/// </summary>
internal static class DatabaseStartupGate
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public static async Task WaitAndEnsureDatabaseAsync(string connectionString, bool useSqlServer, CancellationToken ct = default)
    {
        var delay = InitialDelay;
        var waitingLogged = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (useSqlServer)
                    await EnsureSqlServerDatabaseAsync(connectionString, ct);
                else
                    await EnsurePostgresDatabaseAsync(connectionString, ct);

                if (waitingLogged)
                    Log.Information("Database server reachable and application database present; continuing host startup.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!waitingLogged)
                {
                    Log.Warning("Waiting for the database server before starting the host: {Error}", ex.Message);
                    waitingLogged = true;
                }
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, delay.TotalSeconds * 2));
            }
        }
    }

    // Connect to the server's master catalog (so reachability does not depend on the app DB existing yet),
    // then create the application database if it is missing. QUOTENAME guards the identifier.
    private static async Task EnsureSqlServerDatabaseAsync(string connectionString, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 };
        string database = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        if (string.IsNullOrWhiteSpace(database))
            return;

        // EXEC()'s concatenation only accepts string variables/literals (not a QUOTENAME() call), so build
        // the statement into a variable first. QUOTENAME guards the identifier against injection.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            if db_id(@db) is null
            begin
                declare @sql nvarchar(max) = N'create database ' + quotename(@db);
                exec (@sql);
            end
            """;
        command.Parameters.AddWithValue("@db", database);
        await command.ExecuteNonQueryAsync(ct);
    }

    // Connect to the maintenance "postgres" database, then create the application database if it is missing.
    // CREATE DATABASE cannot be parameterised, so the identifier is quoted explicitly.
    private static async Task EnsurePostgresDatabaseAsync(string connectionString, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 };
        string? database = builder.Database;
        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        if (string.IsNullOrWhiteSpace(database))
            return;

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "select 1 from pg_database where datname = @db";
            check.Parameters.AddWithValue("db", database);
            if (await check.ExecuteScalarAsync(ct) is not null)
                return;
        }

        await using var create = connection.CreateCommand();
        create.CommandText = $"create database {QuotePostgresIdentifier(database)}";
        await create.ExecuteNonQueryAsync(ct);
    }

    private static string QuotePostgresIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
