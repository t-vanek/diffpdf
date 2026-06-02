using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
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
            INetworkShareResolver shareResolver,
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

            // Resolve share aliases / credential profiles up front so the durable
            // pipeline only ever sees concrete, reachable paths and credentials.
            ResolvedFolder oldResolved, newResolved;
            try
            {
                oldResolved = shareResolver.Resolve(request.OldFolder, request.OldFolderCredentials, request.OldFolderCredentialProfile);
                newResolved = shareResolver.Resolve(request.NewFolder, request.NewFolderCredentials, request.NewFolderCredentialProfile);
            }
            catch (NetworkConfigurationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            // Authenticated UNC shares are validated in the worker; only check plain local paths up front.
            if (oldResolved.Credentials is null && !UncPath.IsUnc(oldResolved.Path) && !Directory.Exists(oldResolved.Path))
                return Results.Problem($"Old folder not found: {oldResolved.Path}", statusCode: StatusCodes.Status400BadRequest);
            if (newResolved.Credentials is null && !UncPath.IsUnc(newResolved.Path) && !Directory.Exists(newResolved.Path))
                return Results.Problem($"New folder not found: {newResolved.Path}", statusCode: StatusCodes.Status400BadRequest);

            // Persist the resolved form: aliases expanded, profiles flattened to credentials.
            var resolvedRequest = request with
            {
                OldFolder = oldResolved.Path,
                NewFolder = newResolved.Path,
                OldFolderCredentials = oldResolved.Credentials,
                NewFolderCredentials = newResolved.Credentials,
                OldFolderCredentialProfile = null,
                NewFolderCredentialProfile = null,
            };

            var job = new ComparisonJob
            {
                Id = Guid.NewGuid(),
                Request = resolvedRequest,
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
