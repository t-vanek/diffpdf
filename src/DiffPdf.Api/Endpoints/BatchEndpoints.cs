using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Async batch comparison submission.</summary>
public static class BatchEndpoints
{
    public static void MapBatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/batch", async (
            BatchComparisonRequest request,
            IBusinessInstanceStore instances,
            IProjectStore projects,
            IJobSubmissionService submission,
            CancellationToken ct) =>
        {
            var scope = request.Scope;
            if (!StorageKeyValidator.IsValidKey(scope.BusinessInstanceKey) || !StorageKeyValidator.IsValidKey(scope.ProjectKey))
                return Results.Problem("Invalid businessInstanceKey or projectKey.", statusCode: StatusCodes.Status400BadRequest);

            var instance = await instances.GetByKeyAsync(scope.BusinessInstanceKey, ct);
            if (instance is null)
                return Results.Problem($"Business instance '{scope.BusinessInstanceKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            var project = await projects.GetByKeyAsync(instance.Id, scope.ProjectKey, ct);
            if (project is null)
                return Results.Problem($"Project '{scope.ProjectKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            // Authenticated UNC shares are validated in the worker; only check plain local paths up front.
            if (request.OldFolderCredentials is null && !UncPath.IsUnc(request.OldFolder) && !Directory.Exists(request.OldFolder))
                return Results.Problem($"Old folder not found: {request.OldFolder}", statusCode: StatusCodes.Status400BadRequest);
            if (request.NewFolderCredentials is null && !UncPath.IsUnc(request.NewFolder) && !Directory.Exists(request.NewFolder))
                return Results.Problem($"New folder not found: {request.NewFolder}", statusCode: StatusCodes.Status400BadRequest);

            var job = new ComparisonJob
            {
                Id = Guid.NewGuid(),
                Request = request,
                BusinessInstanceId = instance.Id,
                ProjectId = project.Id,
            };

            await submission.SubmitAsync(job, new RunBatchComparison(job.Id, scope.BusinessInstanceKey, scope.ProjectKey), ct);

            return Results.Accepted($"/api/v1/jobs/{job.Id}", JobSummary.From(job));
        })
        .WithTags("Batch")
        .WithSummary("Submit a folder comparison job")
        .Produces<JobSummary>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
