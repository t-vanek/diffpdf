namespace DiffPdf.Core.Tests.Integration;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped (not failed) when SQL Server LocalDB is unavailable, so the
/// real-database integration tests run wherever LocalDB exists and are quietly skipped everywhere else.
/// </summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (!LocalDb.IsAvailable)
            Skip = "SQL Server LocalDB ((localdb)\\MSSQLLocalDB) is not available on this machine.";
    }
}
