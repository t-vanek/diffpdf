using System.Text.Json.Serialization;
using DiffPdf.Api;
using DiffPdf.Api.Auth;
using DiffPdf.Api.Endpoints;
using DiffPdf.Api.Hubs;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Persistence;
using DiffPdf.Persistence.Postgres.DependencyInjection;
using DiffPdf.Persistence.SqlServer.DependencyInjection;
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
builder.Services.AddProblemDetails();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<PdfWorkLimiterOptions>(builder.Configuration.GetSection("Pdf"));
builder.Services.Configure<NetworkOptions>(builder.Configuration.GetSection(NetworkOptions.SectionName));

// SignalR realtime progress. Registering the publisher before AddDiffPdfWorker
// means the worker's no-op fallback is not used.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IJobProgressPublisher, SignalRJobProgressPublisher>();

builder.Services.AddDiffPdf();
builder.Services.AddDiffPdfWorker();

string? postgres = builder.Configuration.GetConnectionString("Postgres");
string? sqlServer = builder.Configuration.GetConnectionString("SqlServer");
string? rabbit = builder.Configuration.GetConnectionString("RabbitMq");

// SQL Server wins when both relational connection strings are configured.
bool useSqlServer = !string.IsNullOrWhiteSpace(sqlServer);
string? relational = useSqlServer ? sqlServer : postgres;

if (!string.IsNullOrWhiteSpace(relational) && !string.IsNullOrWhiteSpace(rabbit))
{
    // Production / full stack: relational source of truth + RabbitMQ transport via Wolverine.
    if (useSqlServer)
    {
        builder.Services.AddSqlServerPersistence(relational);
        builder.Host.UseWolverine(opts => opts.ConfigureDiffPdfMessaging(rabbit, relational, DiffPdfDatabase.SqlServer));
    }
    else
    {
        builder.Services.AddPostgresPersistence(relational);
        builder.Host.UseWolverine(opts => opts.ConfigureDiffPdfMessaging(rabbit, relational, DiffPdfDatabase.Postgres));
    }
}
else
{
    // Dev fallback: in-memory stores + local (in-process) Wolverine transport.
    builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
    builder.Services.AddSingleton<IFilePairTaskStore, InMemoryFilePairTaskStore>();
    builder.Services.AddSingleton<IBranchStore, InMemoryBranchStore>();
    builder.Services.AddSingleton<IInstanceStore, InMemoryInstanceStore>();
    builder.Services.AddScoped<IJobSubmissionService, SimpleJobSubmissionService>();
    builder.Host.UseWolverine(opts =>
    {
        opts.UseRuntimeCompilation();
        opts.Discovery.IncludeAssembly(typeof(DiffPdfWolverineConfiguration).Assembly);
    });
}

// Recovers file-pair tasks abandoned by a crashed worker (works with either store).
builder.Services.AddHostedService<StaleTaskRecoveryService>();

// On startup, ensure each registered instance's old/new/reports skeleton exists
// (runs after the persistence migration above; no-op for the in-memory fallback).
builder.Services.AddHostedService<InstanceStructureHostedService>();

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
bool authEnabled = auth.Enabled && !string.IsNullOrWhiteSpace(relational);
if (auth.Enabled && string.IsNullOrWhiteSpace(relational))
    Log.Warning("Auth:Enabled is set but no PostgreSQL/SQL Server connection is configured — authentication is disabled.");
if (authEnabled)
    builder.Services.AddDiffPdfAuth(relational!, useSqlServer, auth);

var app = builder.Build();

app.UseSerilogRequestLogging();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapTokenEndpoint(auth);
}

app.MapOpenApi().AllowAnonymous();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "diffpdf v1");
    o.RoutePrefix = "swagger";
});

// Root endpoints (anonymous health/info).
app.MapHealthEndpoints();

// Versioned API surface.
var api = app.MapGroup("/api/v1");
api.MapComparisonEndpoints();
api.MapScopeEndpoints();
api.MapBatchEndpoints();
api.MapJobEndpoints();
api.MapDiscoveryEndpoints();

app.MapHub<JobsHub>("/hubs/jobs");

app.Run();
Log.CloseAndFlush();

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program;
