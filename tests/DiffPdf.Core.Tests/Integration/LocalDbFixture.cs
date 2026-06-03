using DiffPdf.Persistence.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Core.Tests.Integration;

/// <summary>
/// Creates a fresh, uniquely-named database on LocalDB and applies the EF Core migrations (the
/// <c>InitialCreate</c> baseline — the real production schema) once for the whole integration collection,
/// then drops it on teardown. A no-op when LocalDB is unavailable (the tests are skipped instead).
/// </summary>
public sealed class LocalDbFixture : IAsyncLifetime
{
    private readonly string _database = "diffpdf_it_" + Guid.NewGuid().ToString("N");

    public bool Available => LocalDb.IsAvailable;
    public string ConnectionString { get; }

    public LocalDbFixture() => ConnectionString = LocalDb.ConnectionStringFor(_database);

    /// <summary>A new context against the test database — callers dispose it.</summary>
    public DiffPdfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<DiffPdfDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (!Available) return;
        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync(); // creates the database and applies InitialCreate
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;
        try
        {
            await using var ctx = NewContext();
            await ctx.Database.EnsureDeletedAsync();
        }
        catch
        {
            /* best-effort cleanup — a leftover diffpdf_it_* database is harmless */
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LocalDbCollection : ICollectionFixture<LocalDbFixture>
{
    public const string Name = "LocalDb";
}
