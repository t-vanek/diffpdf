using System.Text.Json.Serialization;
using DiffPdf.Api;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using DiffPdf.Worker.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;

// Bootstrap logger captures failures during host construction; replaced below
// by the fully-configured logger read from appsettings.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// File sink path is env-driven so logs can live on a mounted volume (e.g. /data/logs
// in Docker) and survive container restarts. Console + levels stay in appsettings.
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
builder.Services.AddDiffPdf();
builder.Services.AddDiffPdfWorker();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new { service = "diffpdf", status = "ok" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// --- Single pair comparison (synchronous) ---
app.MapPost("/api/comparisons", async (
    SingleComparisonRequest request,
    IComparisonEngine engine,
    IOptions<WorkerOptions> workerOptions,
    CancellationToken ct) =>
{
    if (!File.Exists(request.OldPath))
        return Results.BadRequest(new { error = $"Old file not found: {request.OldPath}" });
    if (!File.Exists(request.NewPath))
        return Results.BadRequest(new { error = $"New file not found: {request.NewPath}" });

    string artifactDir = Path.Combine(workerOptions.Value.ArtifactRoot, "single", Guid.NewGuid().ToString("N"));
    var result = await engine.CompareAsync(request.OldPath, request.NewPath, request.Options, artifactDir, ct);
    return Results.Ok(result);
});

// --- Batch folder comparison (asynchronous job) ---
app.MapPost("/api/batch", async (
    BatchComparisonRequest request,
    IJobStore jobStore,
    IComparisonJobQueue queue,
    CancellationToken ct) =>
{
    // Authenticated UNC shares are connected in the worker, so only validate
    // existence up front for plain local / already-mounted paths.
    if (request.OldFolderCredentials is null && !UncPath.IsUnc(request.OldFolder) && !Directory.Exists(request.OldFolder))
        return Results.BadRequest(new { error = $"Old folder not found: {request.OldFolder}" });
    if (request.NewFolderCredentials is null && !UncPath.IsUnc(request.NewFolder) && !Directory.Exists(request.NewFolder))
        return Results.BadRequest(new { error = $"New folder not found: {request.NewFolder}" });

    var job = await jobStore.CreateAsync(request, ct);
    await queue.EnqueueAsync(job.Id, ct);
    return Results.Accepted($"/api/jobs/{job.Id}", JobSummary.From(job));
});

// --- Job status & listing ---
app.MapGet("/api/jobs", async (IJobStore jobStore, CancellationToken ct) =>
{
    var jobs = await jobStore.ListAsync(ct);
    return Results.Ok(jobs.Select(JobSummary.From));
});

app.MapGet("/api/jobs/{id:guid}", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
{
    var job = await jobStore.GetAsync(id, ct);
    return job is null ? Results.NotFound() : Results.Ok(JobSummary.From(job));
});

app.MapGet("/api/jobs/{id:guid}/report", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
{
    var job = await jobStore.GetAsync(id, ct);
    if (job is null) return Results.NotFound();
    if (job.Report is null)
        return Results.Json(new { status = job.Status.ToString(), message = "Report not ready." }, statusCode: 409);
    return Results.Ok(job.Report);
});

// --- CI gate: pass/fail verdict (200 = passed, 422 = failed) ---
app.MapGet("/api/jobs/{id:guid}/result", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
{
    var job = await jobStore.GetAsync(id, ct);
    if (job is null) return Results.NotFound();
    if (job.Report is null)
        return Results.Json(new { status = job.Status.ToString(), message = "Report not ready." }, statusCode: 409);

    var report = job.Report;
    var payload = new
    {
        passed = report.Passed,
        gated = report.Gate is not null,
        violations = report.GateViolations,
        summary = new
        {
            report.Total,
            report.Identical,
            report.Differing,
            report.OnlyInOld,
            report.OnlyInNew,
            report.Errors,
            report.FilesWithContentErrors,
        },
    };

    // Unprocessable Entity is a convenient non-2xx for `curl --fail` CI gating.
    return report.Passed ? Results.Ok(payload) : Results.Json(payload, statusCode: 422);
});

// --- Artifact download (highlighted diff PDFs) ---
app.MapGet("/api/jobs/{id:guid}/artifacts/{**relativePath}", (
    Guid id,
    string relativePath,
    IOptions<WorkerOptions> workerOptions) =>
{
    string jobRoot = Path.GetFullPath(Path.Combine(workerOptions.Value.ArtifactRoot, id.ToString("N")));
    string requested = Path.GetFullPath(Path.Combine(jobRoot, relativePath));

    // Guard against path traversal outside the job's artifact directory.
    if (!requested.StartsWith(jobRoot, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid path." });
    if (!File.Exists(requested))
        return Results.NotFound();

    return Results.File(requested, "application/pdf", Path.GetFileName(requested));
});

app.Run();
Log.CloseAndFlush();

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program;
