using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Storage;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Endpoints;

/// <summary>Health + synchronous single-pair comparison.</summary>
public static class ComparisonEndpoints
{
    public static void MapComparisonEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new { service = "diffpdf", status = "ok" })).AllowAnonymous();
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

        app.MapPost("/api/comparisons", async (
            SingleComparisonRequest request,
            IComparisonEngine engine,
            IOptions<StorageOptions> storage,
            CancellationToken ct) =>
        {
            if (!File.Exists(request.OldPath))
                return Results.BadRequest(new { error = $"Old file not found: {request.OldPath}" });
            if (!File.Exists(request.NewPath))
                return Results.BadRequest(new { error = $"New file not found: {request.NewPath}" });

            string artifactDir = Path.Combine(storage.Value.RootPath, "single", Guid.NewGuid().ToString("N"));
            var result = await engine.CompareAsync(request.OldPath, request.NewPath, request.Options, artifactDir, ct);
            return Results.Ok(result);
        });
    }
}
