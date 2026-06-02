using System.Text.Json.Serialization;
using DiffPdf.Api;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Pdf.DependencyInjection;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using DiffPdf.Worker.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddOpenApi();
builder.Services.AddDiffPdf();
builder.Services.AddDiffPdfWorker();

var app = builder.Build();

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
    if (!Directory.Exists(request.OldFolder))
        return Results.BadRequest(new { error = $"Old folder not found: {request.OldFolder}" });
    if (!Directory.Exists(request.NewFolder))
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

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program;
