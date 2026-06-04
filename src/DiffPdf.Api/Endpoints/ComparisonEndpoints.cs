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
    }
}
