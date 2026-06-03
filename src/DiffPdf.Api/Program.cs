using System.Text.Json.Serialization;
using DiffPdf.Api;
using DiffPdf.Api.Auth;
using DiffPdf.Api.Endpoints;
using DiffPdf.Api.Hubs;
using DiffPdf.Api.Operational;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging;
using DiffPdf.Messaging.Retention;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Messaging.Triggers;
using DiffPdf.Notifications.DependencyInjection;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Persistence;
using DiffPdf.Persistence.Postgres.DependencyInjection;
using DiffPdf.Persistence.SqlServer.DependencyInjection;
using DiffPdf.Worker.DependencyInjection;
using Serilog;
using Wolverine;

// Resolve the log directory to an absolute path so it is stable regardless of the process working
// directory (a Windows Service starts in System32, not the install folder). Override with DIFFPDF_LOG_DIR.
string logDirectory = Environment.GetEnvironmentVariable("DIFFPDF_LOG_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "logs");
const string logOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

// Bootstrap logger writes to console AND file, so messages emitted before the host starts — notably the
// database startup gate below — are captured even when no console is attached (Windows Service / IIS).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "diffpdf-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: logOutputTemplate)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// Run under the Windows Service Control Manager when launched as a service; a no-op as a console process.
builder.Services.AddWindowsService(options => options.ServiceName = "DiffPdf API");

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logDirectory, "diffpdf-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: logOutputTemplate));

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

// Per-replica heartbeat registry: each automation background service records its ticks here,
// and the operational status endpoint reads the snapshot.
builder.Services.AddSingleton<IAutomationHeartbeat, AutomationHeartbeat>();

string? postgres = builder.Configuration.GetConnectionString("Postgres");
string? sqlServer = builder.Configuration.GetConnectionString("SqlServer");

// SQL Server wins when both relational connection strings are configured.
bool useSqlServer = !string.IsNullOrWhiteSpace(sqlServer);
string? relational = useSqlServer ? sqlServer : postgres;

if (!string.IsNullOrWhiteSpace(relational))
{
    // Production / full stack: relational source of truth + DB-backed durable local queues
    // (no external broker) via Wolverine.
    if (useSqlServer)
    {
        builder.Services.AddSqlServerPersistence(relational);
        builder.Host.UseWolverine(opts => opts.ConfigureDiffPdfMessaging(relational, DiffPdfDatabase.SqlServer));
    }
    else
    {
        builder.Services.AddPostgresPersistence(relational);
        builder.Host.UseWolverine(opts => opts.ConfigureDiffPdfMessaging(relational, DiffPdfDatabase.Postgres));
    }
}
else
{
    // Dev fallback: in-memory stores + local (in-process) Wolverine transport.
    builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
    builder.Services.AddSingleton<IFilePairTaskStore, InMemoryFilePairTaskStore>();
    builder.Services.AddSingleton<IBranchStore, InMemoryBranchStore>();
    builder.Services.AddSingleton<IInstanceStore, InMemoryInstanceStore>();
    builder.Services.AddSingleton<IScheduleStore, InMemoryScheduleStore>();
    builder.Services.AddSingleton<ISubscriptionStore, InMemorySubscriptionStore>();
    builder.Services.AddSingleton<IScheduleRunStore, InMemoryScheduleRunStore>();
    builder.Services.AddSingleton<IWatchStore, InMemoryWatchStore>();
    builder.Services.AddSingleton<ILeaderElection, InMemoryLeaderElection>();
    builder.Services.AddScoped<IJobSubmissionService, SimpleJobSubmissionService>();
    builder.Host.UseWolverine(opts =>
    {
        opts.UseRuntimeCompilation();
        opts.Discovery.IncludeAssembly(typeof(DiffPdfWolverineConfiguration).Assembly);
    });
}

// Operational visibility: persistence backend name + the status/readiness composer (singleton;
// resolves scoped stores per request and caches the renderer probe).
string persistenceProvider = string.IsNullOrWhiteSpace(relational) ? "In-memory" : useSqlServer ? "SQL Server" : "PostgreSQL";
builder.Services.AddSingleton(new PersistenceInfo(persistenceProvider));
builder.Services.AddSingleton<OperationalStatusService>();

// Recovers file-pair tasks abandoned by a crashed worker (works with either store).
builder.Services.AddHostedService<StaleTaskRecoveryService>();

// On startup, ensure each registered instance's old/new/reports skeleton exists
// (runs after the persistence migration above; no-op for the in-memory fallback).
builder.Services.AddHostedService<InstanceStructureHostedService>();

// Automation: outbound notifications (DB-backed subscriptions), a DB-backed recurring
// scheduler, and folder-watch triggers. All are no-ops until configured / populated.
builder.Services.AddDiffPdfNotifications(builder.Configuration);
builder.Services.AddDiffPdfScheduling();
builder.Services.AddDiffPdfFolderWatch();
builder.Services.AddDiffPdfScopeSync(builder.Configuration);
builder.Services.AddDiffPdfRetention(builder.Configuration);

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
api.MapScopeSyncEndpoints();
api.MapScheduleEndpoints();
api.MapSubscriptionEndpoints();
api.MapJobEndpoints();
api.MapDiscoveryEndpoints();
api.MapTriggerEndpoints();
api.MapWatchEndpoints();
api.MapStatusEndpoints();

app.MapHub<JobsHub>("/hubs/jobs");

// Wolverine and the EF stores require the relational database to exist and be reachable at startup
// (Wolverine provisions its inbox/outbox in StartAsync and cannot tolerate a missing database). Rather
// than crash-loop while the server is briefly unavailable, block here — keeping the process alive and
// logging — until the server is reachable, then create the application database if it is missing, before
// starting the host. Skipped for the in-memory dev/test fallback. NOTE: while waiting, Kestrel is not yet
// listening; as a Windows Service, make the service depend on the database service
// (sc config DiffPdfApi depend= MSSQLSERVER) and enable Recovery → Restart so a longer outage self-heals.
if (!string.IsNullOrWhiteSpace(relational))
    await DatabaseStartupGate.WaitAndEnsureDatabaseAsync(relational!, useSqlServer);

app.Run();
Log.CloseAndFlush();

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program;
