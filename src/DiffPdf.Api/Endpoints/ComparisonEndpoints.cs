using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffPdf.Api.Operational;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Endpoints;

/// <summary>Root health/info endpoints and the synchronous single-pair comparison.</summary>
public static class ComparisonEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Ok(new { service = "diffpdf", status = "ok" }))
            .AllowAnonymous().WithTags("Health").WithSummary("Service info").ExcludeFromDescription();

        // Liveness — cheap, dependency-free, always 200 while the process serves HTTP. Also advertises
        // the effective auth state so anonymous clients know whether to prompt for credentials.
        app.MapGet("/health", (ServerAuthInfo authInfo) => Results.Ok(new
            {
                status = "healthy",
                version = BuildInfo.Version,
                uptimeSeconds = BuildInfo.UptimeSeconds,
                authEnabled = authInfo.AuthEnabled,
            }))
            .AllowAnonymous().WithTags("Health").WithSummary("Liveness probe");

        // Readiness — checks critical dependencies (DB / renderer / storage); 200 ready, 503 degraded.
        app.MapGet("/health/ready", async (OperationalStatusService status, CancellationToken ct) =>
        {
            var (ready, body) = await status.BuildReadinessAsync(ct);
            return ready ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().WithTags("Health").WithSummary("Readiness probe (200 ready / 503 degraded)")
          .Produces<ReadinessResponse>().Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    public static void MapComparisonEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/comparisons", async (
            SingleComparisonRequest request,
            IComparisonEngine engine,
            IOptions<StorageOptions> storage,
            CancellationToken ct) =>
        {
            if (!File.Exists(request.OldPath))
                return Results.Problem($"Old file not found: {request.OldPath}", statusCode: StatusCodes.Status400BadRequest);
            if (!File.Exists(request.NewPath))
                return Results.Problem($"New file not found: {request.NewPath}", statusCode: StatusCodes.Status400BadRequest);

            string artifactDir = Path.Combine(storage.Value.RootPath, "single", Guid.NewGuid().ToString("N"));
            var result = await engine.CompareAsync(request.OldPath, request.NewPath, request.Options, artifactDir, ct);
            return Results.Ok(result);
        })
        .WithTags("Comparison")
        .WithSummary("Compare a single old/new PDF pair (synchronous)")
        .Produces<FileComparisonResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        // Preview variant: renders the highlighted diff and streams the PDF directly (application/pdf), with the
        // verdict in ASCII-safe X-Diff-* response headers. Lets the desktop show the diff in an embedded viewer
        // instead of raw JSON. 204 when the documents are identical (no highlight to show); 422 when a file
        // cannot be compared (corrupt/unreadable).
        group.MapPost("/comparisons/preview", async (
            SingleComparisonRequest request,
            IComparisonEngine engine,
            IOptions<StorageOptions> storage,
            HttpResponse response,
            CancellationToken ct) =>
        {
            if (!File.Exists(request.OldPath))
                return Results.Problem($"Old file not found: {request.OldPath}", statusCode: StatusCodes.Status400BadRequest);
            if (!File.Exists(request.NewPath))
                return Results.Problem($"New file not found: {request.NewPath}", statusCode: StatusCodes.Status400BadRequest);

            string artifactDir = Path.Combine(storage.Value.RootPath, "single", Guid.NewGuid().ToString("N"));
            return await RunPreviewAsync(engine, request.OldPath, request.NewPath, request.Options, artifactDir, response, ct);
        })
        .WithTags("Comparison")
        .WithSummary("Compare a single pair (paths) and stream the highlighted diff PDF (verdict in X-Diff-* headers)")
        .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Upload variant for remote clients: the desktop posts the two PDFs as multipart/form-data (in production
        // the server and client are on different machines, so a server-side path would not resolve). Otherwise
        // identical to /preview — streams the highlighted diff PDF + verdict in X-Diff-* headers.
        group.MapPost("/comparisons/preview-upload", async (
            HttpRequest request,
            IComparisonEngine engine,
            IOptions<StorageOptions> storage,
            HttpResponse response,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.Problem("Expected multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);

            var form = await request.ReadFormAsync(ct);
            var oldFile = form.Files["old"];
            var newFile = form.Files["new"];
            if (oldFile is null || newFile is null)
                return Results.Problem("Both 'old' and 'new' PDF files are required.", statusCode: StatusCodes.Status400BadRequest);

            string artifactDir = Path.Combine(storage.Value.RootPath, "single", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(artifactDir);
            // Save under the (sanitised) original names so the diff PDF is named after the new file.
            string oldPath = await SaveUploadAsync(oldFile, artifactDir, "old.pdf", ct);
            string newPath = await SaveUploadAsync(newFile, artifactDir, "new.pdf", ct);

            return await RunPreviewAsync(engine, oldPath, newPath, ParseOptions(form["options"].ToString()), artifactDir, response, ct);
        })
        .DisableAntiforgery()
        .WithTags("Comparison")
        .WithSummary("Compare two uploaded PDFs and stream the highlighted diff (verdict in X-Diff-* headers)")
        .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Shared body of both preview endpoints: runs the comparison (highlight forced on), maps a failed
    /// render/parse to a clean error instead of an unhandled 500 stack, and streams the diff PDF + verdict headers
    /// (204 when identical).</summary>
    private static async Task<IResult> RunPreviewAsync(
        IComparisonEngine engine, string oldPath, string newPath, ComparisonOptions options,
        string artifactDir, HttpResponse response, CancellationToken ct)
    {
        FileComparisonResult result;
        try
        {
            result = await engine.CompareAsync(oldPath, newPath, options with { ProduceHighlightedPdf = true }, artifactDir, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // e.g. the Ghostscript renderer is not installed/usable on the server — surface a readable message
            // (the desktop shows this) rather than a raw 500 stack trace.
            return Results.Problem("Comparison failed: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        if (result.Error is { Length: > 0 })
            return Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);

        WriteVerdictHeaders(response, result);
        return result.HighlightedPdfPath is { } abs && File.Exists(abs)
            ? Results.File(abs, "application/pdf", Path.GetFileName(abs))
            : Results.NoContent();
    }

    private static readonly JsonSerializerOptions PreviewJson =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Deserialises the comparer options from the multipart "options" field (camelCase JSON); empty → defaults.</summary>
    private static ComparisonOptions ParseOptions(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ComparisonOptions()
            : JsonSerializer.Deserialize<ComparisonOptions>(json, PreviewJson) ?? new ComparisonOptions();

    /// <summary>Writes an uploaded file into <paramref name="dir"/> under its sanitised name (Path.GetFileName strips
    /// any directory components), returning the absolute path. Falls back to <paramref name="fallbackName"/>.</summary>
    private static async Task<string> SaveUploadAsync(IFormFile file, string dir, string fallbackName, CancellationToken ct)
    {
        var name = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(name)) name = fallbackName;
        var path = Path.Combine(dir, name);
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, ct);
        return path;
    }

    /// <summary>Emits the comparison verdict as ASCII-safe X-Diff-* response headers (shared by both preview endpoints).</summary>
    private static void WriteVerdictHeaders(HttpResponse response, FileComparisonResult result)
    {
        response.Headers["X-Diff-Identical"] = result.AreIdentical ? "true" : "false";
        response.Headers["X-Diff-Old-Pages"] = result.OldPageCount.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Diff-New-Pages"] = result.NewPageCount.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Diff-Differing"] = result.DifferingPages.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Diff-Similarity"] = result.Similarity.ToString("0.####", CultureInfo.InvariantCulture);
        response.Headers["X-Diff-Content-Errors"] = result.ContentErrors.Count.ToString(CultureInfo.InvariantCulture);
    }
}
