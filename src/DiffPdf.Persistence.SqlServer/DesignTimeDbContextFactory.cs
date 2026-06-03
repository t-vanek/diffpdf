using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DiffPdf.Persistence.SqlServer;

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
            ?? "Server=localhost;Database=diffpdf;User Id=sa;Password=Your_password123;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<DiffPdfDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new DiffPdfDbContext(options);
    }
}
