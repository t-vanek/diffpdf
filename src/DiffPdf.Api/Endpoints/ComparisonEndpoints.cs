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
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
            .AllowAnonymous().WithTags("Health").WithSummary("Liveness probe");
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
    }
}
