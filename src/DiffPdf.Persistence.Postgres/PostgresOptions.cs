namespace DiffPdf.Persistence.Postgres;

public sealed class PostgresOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Run the schema migration on startup.</summary>
    public bool AutoMigrate { get; set; } = true;
}
