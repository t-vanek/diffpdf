using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using DiffPdf.Api;
using DiffPdf.Api.Auth;
using DiffPdf.Api.Discovery;
using DiffPdf.Api.Endpoints;
using DiffPdf.Api.Hubs;
using DiffPdf.Api.Operational;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Messaging.Observability;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Messaging.Triggers;
using DiffPdf.Notifications.DependencyInjection;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Persistence;
using DiffPdf.Persistence.Postgres.DependencyInjection;
using DiffPdf.Persistence.SqlServer.DependencyInjection;
using DiffPdf.Worker.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

// OpenTelemetry: traces + metrics for ASP.NET Core, outbound HTTP, EF Core, Wolverine and the .NET runtime,
// plus our own DiffPdf.Queue meter. Always scrapable at /metrics (Prometheus); additionally exported via OTLP
// when OTEL_EXPORTER_OTLP_ENDPOINT is set. The Wolverine *metrics* meter is named "Wolverine:{AppName}", so it
// must be matched with the wildcard "Wolverine*" (a bare "Wolverine" captures nothing); the *trace* source is
// just "Wolverine".
bool otlpConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("DiffPdf.Api", serviceVersion: BuildInfo.Version))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource("Wolverine");
        if (otlpConfigured) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Wolverine*")
            .AddMeter(DiffPdfMetrics.MeterName)
            .AddMeter("DiffPdf.Render")
            .AddMeter("DiffPdf.Compare");
        m.AddPrometheusExporter(); // exposes /metrics (mapped below)
        if (otlpConfigured) m.AddOtlpExporter();
    });
builder.Services.AddProblemDetails();

// Rate limiting: throttle the expensive write endpoints (scope sync, triggers, branch fan-out). Returns 429
// when the per-minute window is exceeded; read/health endpoints are unthrottled.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("expensive", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 0;
    });
});
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<PdfWorkLimiterOptions>(builder.Configuration.GetSection("Pdf"));
builder.Services.Configure<NetworkOptions>(builder.Configuration.GetSection(NetworkOptions.SectionName));

// SignalR realtime progress. Registering the publisher before AddDiffPdfWorker
// means the worker's no-op fallback is not used.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IJobProgressPublisher, SignalRJobProgressPublisher>();
builder.Services.AddSingleton<ITriggerEventPublisher, SignalRTriggerEventPublisher>();
builder.Services.AddSingleton<IBranchQueueStatePublisher, SignalRBranchQueueStatePublisher>();

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
    builder.Services.AddSingleton<ISubscriptionStore, InMemorySubscriptionStore>();
    builder.Services.AddSingleton<IControlCheckStore, InMemoryControlCheckStore>();
    builder.Services.AddSingleton<IControlCheckRunStore, InMemoryControlCheckRunStore>();
    builder.Services.AddSingleton<ITriggerStore, InMemoryTriggerStore>();
    builder.Services.AddSingleton<ITriggerRunStore, InMemoryTriggerRunStore>();
    builder.Services.AddSingleton<IAuditLogStore, InMemoryAuditLogStore>();
    builder.Services.AddSingleton<IScopeConfigurationStore, InMemoryScopeConfigurationStore>();
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

// Per-branch / per-instance statistics for the detail views (scoped: depends on the per-request stores).
builder.Services.AddScoped<ScopeStatsService>();

// Recovers file-pair tasks abandoned by a crashed worker (works with either store).
builder.Services.AddHostedService<StaleTaskRecoveryService>();

// On startup, ensure each registered instance's old/new/reports skeleton exists
// (runs after the persistence migration above; no-op for the in-memory fallback).
builder.Services.AddHostedService<InstanceStructureHostedService>();

// Outbound notifications (DB-backed subscriptions) + the on-demand batch launcher used by the triggers.
builder.Services.AddDiffPdfNotifications(builder.Configuration);
builder.Services.AddScoped<IBatchLauncher, BatchLauncher>();
builder.Services.AddScoped<ITriggerService, TriggerService>();
builder.Services.AddScoped<ITriggerProvisioner, TriggerProvisioner>();
builder.Services.AddDiffPdfScopeConfiguration();
builder.Services.AddDiffPdfBranchQueue();
builder.Services.AddDiffPdfScopeSync(builder.Configuration);

// Unified control/monitoring mechanism: runtime-configured checks (readiness, health, structure-sync,
// retention) executed on a cadence by one leader-gated runner. Idle until checks are created via the API.
builder.Services.AddDiffPdfControlPlane(builder.Configuration);

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
bool authEnabled = auth.Enabled && !string.IsNullOrWhiteSpace(relational);
builder.Services.AddSingleton(new ServerAuthInfo(authEnabled));
if (auth.Enabled && string.IsNullOrWhiteSpace(relational))
    Log.Warning("Auth:Enabled is set but no PostgreSQL/SQL Server connection is configured — authentication is disabled.");
if (authEnabled)
    builder.Services.AddDiffPdfAuth(useSqlServer, auth);

// LAN server discovery: answer UDP broadcast probes so the desktop client can auto-find this server.
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
if ((builder.Configuration.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>() ?? new DiscoveryOptions()).Enabled)
    builder.Services.AddHostedService<ServerDiscoveryResponder>();

var app = builder.Build();

// Turn unhandled exceptions into clean RFC-7807 ProblemDetails (no stack-trace leak); pairs with AddProblemDetails().
app.UseExceptionHandler();

app.UseSerilogRequestLogging();
app.UseRateLimiter();

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

// Prometheus scrape endpoint (/metrics) — anonymous, for a local collector on the private LAN.
app.MapPrometheusScrapingEndpoint();

// Versioned API surface.
var api = app.MapGroup("/api/v1");
api.MapComparisonEndpoints();
api.MapScopeEndpoints();
api.MapScopeSyncEndpoints();
api.MapSubscriptionEndpoints();
api.MapJobEndpoints();
api.MapDiscoveryEndpoints();
api.MapTriggerEndpoints();
api.MapStatusEndpoints();
api.MapControlCheckEndpoints();
api.MapScopeConfigurationEndpoints();
api.MapBranchQueueEndpoints();

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
