using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using DiffPdf.Api;
using DiffPdf.Api.Auth;
using DiffPdf.Api.Discovery;
using DiffPdf.Api.Endpoints;
using DiffPdf.Api.Hubs;
using DiffPdf.Api.Operational;
using DiffPdf.Application;
using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Files;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging;
using DiffPdf.Messaging.Automations;
using DiffPdf.Messaging.Observability;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Notifications;
using DiffPdf.Notifications.DependencyInjection;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Pdf.Rendering;
using DiffPdf.Persistence;
using DiffPdf.Persistence.SqlServer.DependencyInjection;
using DiffPdf.Worker;
using DiffPdf.Worker.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Wolverine;

// The SCM-facing process must reach Running even when SQL Server is temporarily unavailable. It supervises
// the actual API child process and performs the database wait after Windows has completed service startup.
if (WindowsServiceBootstrap.ShouldRun(args))
{
    await WindowsServiceBootstrap.RunAsync(args);
    return;
}

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
            .AddSource("Wolverine")
            .AddSource("DiffPdf.Comparison"); // domain spans for the per-pair comparison (DiffPdfTracing.Source)
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
            .AddMeter("DiffPdf.Compare")
            .AddMeter("DiffPdf.Engine") // per-phase engine histograms (probe/extract/pixel/blank/highlight)
            .AddMeter(AutomationMetrics.MeterName); // automation runs/steps/failing gauge
        m.AddPrometheusExporter(); // exposes /metrics (mapped below)
        if (otlpConfigured) m.AddOtlpExporter();
    });
builder.Services.AddProblemDetails();

// Compress API responses (brotli/gzip; the defaults already include application/json) — the jobs list, paged
// task pages and reports are sizable JSON over the LAN. Enabled for HTTPS too: a LAN data API, not secret-bearing HTML.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

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
// PDF file manager (the desktop "Správa souborů" page). The multipart form cap must cover the configured
// per-file upload limit — ReadFormAsync enforces FormOptions, while the per-request Kestrel body cap
// is raised on the upload endpoint itself.
builder.Services.Configure<FileManagerOptions>(builder.Configuration.GetSection(FileManagerOptions.SectionName));
var fileManagerOptions = builder.Configuration.GetSection(FileManagerOptions.SectionName).Get<FileManagerOptions>() ?? new FileManagerOptions();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = Math.Max(o.MultipartBodyLengthLimit, fileManagerOptions.MaxUploadSizeBytes + 1024 * 1024));
// Renderer options were registered (AddDiffPdf) but never bound — without these lines every
// Ghostscript:*/Pdfium:* key (timeouts, block size, caches) was silently ignored.
builder.Services.Configure<GhostscriptOptions>(builder.Configuration.GetSection("Ghostscript"));
builder.Services.Configure<PdfiumOptions>(builder.Configuration.GetSection("Pdfium"));
builder.Services.Configure<NetworkOptions>(builder.Configuration.GetSection(NetworkOptions.SectionName));

// SignalR realtime progress. Registering the publisher before AddDiffPdfWorker
// means the worker's no-op fallback is not used.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IJobProgressPublisher, SignalRJobProgressPublisher>();
builder.Services.AddSingleton<ITriggerEventPublisher, SignalRTriggerEventPublisher>();
builder.Services.AddSingleton<IBranchQueueStatePublisher, SignalRBranchQueueStatePublisher>();
builder.Services.AddSingleton<ISystemEventPublisher, SignalRSystemEventPublisher>();
// Append-only system event log (job outcomes, automation runs, dead-letters, recovery zásahy) + realtime
// push; the store is provider-specific (registered below), the log itself is provider-agnostic.
builder.Services.AddScoped<ISystemEventLog, SystemEventLog>();

builder.Services.AddDiffPdf();
builder.Services.AddDiffPdfWorker();

// AddDiffPdfWorker registers WorkerOptions with code defaults only — bind the Worker section here so
// appsettings / env vars (Worker__*) actually take effect; the same values drive the queue parallelism below.
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
var workerOptions = builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions();

// Per-replica heartbeat registry: each automation background service records its ticks here,
// and the operational status endpoint reads the snapshot.
builder.Services.AddSingleton<IAutomationHeartbeat, AutomationHeartbeat>();

string? relational = builder.Configuration.GetConnectionString("SqlServer");

if (!string.IsNullOrWhiteSpace(relational))
{
    // Production / full stack: SQL Server source of truth + DB-backed durable local queues
    // (no external broker) via Wolverine.
    builder.Services.AddSqlServerPersistence(relational);
    builder.Host.UseWolverine(opts => opts.ConfigureDiffPdfMessaging(relational, workerOptions));
}
else
{
    // Dev fallback: in-memory stores + local (in-process) Wolverine transport.
    builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
    builder.Services.AddSingleton<IFilePairTaskStore, InMemoryFilePairTaskStore>();
    builder.Services.AddSingleton<IBranchStore, InMemoryBranchStore>();
    builder.Services.AddSingleton<IInstanceStore, InMemoryInstanceStore>();
    builder.Services.AddSingleton<ISubscriptionStore, InMemorySubscriptionStore>();
    builder.Services.AddSingleton<INotificationDeliveryStore, InMemoryNotificationDeliveryStore>();
    builder.Services.AddSingleton<ISystemEventStore, InMemorySystemEventStore>();
    builder.Services.AddSingleton<IEmailSettingsStore, InMemoryEmailSettingsStore>();
    builder.Services.AddSingleton<IAutomationStore, InMemoryAutomationStore>();
    builder.Services.AddSingleton<IAutomationRunStore, InMemoryAutomationRunStore>();
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
string persistenceProvider = string.IsNullOrWhiteSpace(relational) ? "In-memory" : "SQL Server";
builder.Services.AddSingleton(new PersistenceInfo(persistenceProvider));
builder.Services.AddSingleton<OperationalStatusService>();

// Recovers file-pair tasks abandoned by a crashed worker (works with either store).
builder.Services.AddHostedService<StaleTaskRecoveryService>();

// Bookends worker lifetime for fast interrupt recovery: on graceful shutdown returns this process's in-flight
// pairs to the queue (Wolverine redelivers + re-compares on restart, in seconds rather than the ~12-min lease);
// on startup reclaims orphaned Running pairs left by a hard crash (single-instance; see ReclaimOrphansOnStartup).
builder.Services.AddHostedService<WorkerLifecycleService>();

// Leader-gated watchdog: alerts (notify-only) on jobs stalled mid-comparison and feeds the stuck-job +
// active-task backlog gauges. Idle when StuckJobWatchdog:Enabled=false.
builder.Services.AddOptions<StuckJobWatchdogOptions>()
    .Bind(builder.Configuration.GetSection(StuckJobWatchdogOptions.SectionName))
    .Validate(o => o.IntervalSeconds > 0, "StuckJobWatchdog:IntervalSeconds must be > 0.")
    .Validate(o => o.StallThresholdMinutes > 0, "StuckJobWatchdog:StallThresholdMinutes must be > 0.")
    .ValidateOnStart();
builder.Services.AddHostedService<JobStalledWatchdogService>();

// On startup, ensure each registered instance's old/new/reports skeleton exists
// (runs after the persistence migration above; no-op for the in-memory fallback).
builder.Services.AddHostedService<InstanceStructureHostedService>();

// Outbound notifications (DB-backed subscriptions) + the on-demand batch launcher used by the triggers.
builder.Services.AddDiffPdfNotifications(builder.Configuration);
// Notification outbox sender: the dispatcher only appends rows; this leader-gated service e-mails them
// with retries/backoff and parks exhausted rows as DeadLetter (visible + re-sendable in the client).
builder.Services.AddOptions<NotificationDeliveryOptions>()
    .Bind(builder.Configuration.GetSection(NotificationDeliveryOptions.SectionName))
    .Validate(o => o.IntervalSeconds > 0, "NotificationDelivery:IntervalSeconds must be > 0.")
    .Validate(o => o.MaxAttempts > 0, "NotificationDelivery:MaxAttempts must be > 0.")
    .Validate(o => o.BatchSize > 0, "NotificationDelivery:BatchSize must be > 0.")
    .ValidateOnStart();
builder.Services.AddHostedService<NotificationDeliveryService>();
builder.Services.AddScoped<IBatchLauncher, BatchLauncher>();
builder.Services.AddScoped<IFilePairRequeueDispatcher, FilePairRequeueDispatcher>();
// Application layer: scope/job/check/subscription/trigger/config orchestration (drives the endpoints).
builder.Services.AddDiffPdfApplication();
builder.Services.AddDiffPdfBranchQueue();
builder.Services.AddDiffPdfScopeSync(builder.Configuration);

// Unified control/monitoring mechanism: runtime-configured checks (readiness, health, structure-sync,
// retention) executed on a cadence by one leader-gated runner. Idle until checks are created via the API.
builder.Services.AddDiffPdfAutomationEngine(builder.Configuration);

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
bool authEnabled = auth.Enabled && !string.IsNullOrWhiteSpace(relational);
builder.Services.AddSingleton(new ServerAuthInfo(authEnabled));
if (auth.Enabled && string.IsNullOrWhiteSpace(relational))
    Log.Warning("Auth:Enabled is set but no SQL Server connection is configured — authentication is disabled.");
if (authEnabled)
    builder.Services.AddDiffPdfAuth(auth);

// LAN server discovery: answer UDP broadcast probes so the desktop client can auto-find this server.
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
if ((builder.Configuration.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>() ?? new DiscoveryOptions()).Enabled)
    builder.Services.AddHostedService<ServerDiscoveryResponder>();

var app = builder.Build();

// Compress responses — outermost, so it wraps everything downstream.
app.UseResponseCompression();

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
api.MapEmailSettingsEndpoints();
api.MapNotificationDeliveryEndpoints();
api.MapSystemEventEndpoints();
api.MapJobEndpoints();
api.MapDiscoveryEndpoints();
api.MapTriggerEndpoints();
api.MapStatusEndpoints();
api.MapAutomationEndpoints();
api.MapScopeConfigurationEndpoints();
api.MapBranchQueueEndpoints();
api.MapFileEndpoints();

app.MapHub<JobsHub>("/hubs/jobs");

app.Run();
Log.CloseAndFlush();

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program;
