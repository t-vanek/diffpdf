using System.Text.Json.Serialization;
using DiffPdf.Api.Endpoints;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Persistence;
using DiffPdf.Persistence.Postgres.DependencyInjection;
using DiffPdf.Worker.DependencyInjection;
using Serilog;
using Wolverine;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

string logDirectory = Environment.GetEnvironmentVariable("DIFFPDF_LOG_DIR") ?? "logs";
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logDirectory, "diffpdf-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddOpenApi();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddDiffPdf();
builder.Services.AddDiffPdfWorker();

string? postgres = builder.Configuration.GetConnectionString("Postgres");
string? rabbit = builder.Configuration.GetConnectionString("RabbitMq");

if (!string.IsNullOrWhiteSpace(postgres) && !string.IsNullOrWhiteSpace(rabbit))
{
    // Production / full stack: PostgreSQL source of truth + RabbitMQ transport via Wolverine.
    builder.Services.AddPostgresPersistence(postgres);
    builder.Host.UseWolverine(opts => opts.ConfigureDiffPdfMessaging(rabbit, postgres));
}
else
{
    // Dev fallback: in-memory stores + local (in-process) Wolverine transport.
    builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
    builder.Services.AddSingleton<IBusinessInstanceStore, InMemoryBusinessInstanceStore>();
    builder.Services.AddSingleton<IProjectStore, InMemoryProjectStore>();
    builder.Host.UseWolverine(opts =>
    {
        opts.UseRuntimeCompilation();
        opts.Discovery.IncludeAssembly(typeof(DiffPdfWolverineConfiguration).Assembly);
    });
}

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapOpenApi();

app.MapComparisonEndpoints();
app.MapScopeEndpoints();
app.MapBatchEndpoints();
app.MapJobEndpoints();

app.Run();
Log.CloseAndFlush();

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program;
