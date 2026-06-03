using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DiffPdf.Persistence.Postgres;

/// <summary>
/// Lets the EF Core CLI (<c>dotnet ef migrations add …</c>) build the context without booting the API
/// host and its Wolverine wiring. The connection string is only used at design time; scaffolding a
/// migration does not connect to it. Override via the <c>DIFFPDF_DESIGN_CONNECTION</c> environment variable.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DiffPdfDbContext>
{
    public DiffPdfDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("DIFFPDF_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=diffpdf;Username=diffpdf;Password=diffpdf";

        var options = new DbContextOptionsBuilder<DiffPdfDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new DiffPdfDbContext(options);
    }
}
