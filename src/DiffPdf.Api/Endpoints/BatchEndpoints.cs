using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Wolverine;

namespace DiffPdf.Api.Endpoints;

/// <summary>Async batch comparison submission.</summary>
public static class BatchEndpoints
{
    public static void MapBatchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/batch", async (
            BatchComparisonRequest request,
            IBusinessInstanceStore instances,
            IProjectStore projects,
            IJobSubmissionService submission,
            CancellationToken ct) =>
        {
            var scope = request.Scope;
            if (!StorageKeyValidator.IsValidKey(scope.BusinessInstanceKey) || !StorageKeyValidator.IsValidKey(scope.ProjectKey))
                return Results.BadRequest(new { error = "Invalid businessInstanceKey or projectKey." });

            var instance = await instances.GetByKeyAsync(scope.BusinessInstanceKey, ct);
            if (instance is null)
                return Results.NotFound(new { error = $"Business instance '{scope.BusinessInstanceKey}' not found." });

            var project = await projects.GetByKeyAsync(instance.Id, scope.ProjectKey, ct);
            if (project is null)
                return Results.NotFound(new { error = $"Project '{scope.ProjectKey}' not found." });

            // Authenticated UNC shares are validated in the worker; only check plain local paths up front.
            if (request.OldFolderCredentials is null && !UncPath.IsUnc(request.OldFolder) && !Directory.Exists(request.OldFolder))
                return Results.BadRequest(new { error = $"Old folder not found: {request.OldFolder}" });
            if (request.NewFolderCredentials is null && !UncPath.IsUnc(request.NewFolder) && !Directory.Exists(request.NewFolder))
                return Results.BadRequest(new { error = $"New folder not found: {request.NewFolder}" });

            var job = new ComparisonJob
            {
                Id = Guid.NewGuid(),
                Request = request,
                BusinessInstanceId = instance.Id,
                ProjectId = project.Id,
            };

            await submission.SubmitAsync(job, new RunBatchComparison(job.Id, scope.BusinessInstanceKey, scope.ProjectKey), ct);

            return Results.Accepted($"/api/jobs/{job.Id}", JobSummary.From(job));
        });
    }
}
