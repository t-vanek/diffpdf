using Microsoft.Data.SqlClient;

namespace DiffPdf.Core.Tests.Integration;

/// <summary>
/// Detects SQL Server LocalDB once per test run so the real-database integration tests skip cleanly on
/// machines (e.g. CI without LocalDB) where it is not installed, rather than failing.
/// </summary>
internal static class LocalDb
{
    /// <summary>The LocalDB instance every integration test connects through (each test run uses its own database).</summary>
    public const string Instance = @"(localdb)\MSSQLLocalDB";

    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using var connection = new SqlConnection(
                $"Server={Instance};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5");
            connection.Open();
            return true;
        }
        catch
        {
            return false; // LocalDB not installed / not startable — integration tests will be skipped
        }
    });

    public static bool IsAvailable => Probe.Value;

    public static string ConnectionStringFor(string database) =>
        $"Server={Instance};Database={database};Integrated Security=true;TrustServerCertificate=true";
}
