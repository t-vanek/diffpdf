using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Async batch comparison submission. The old/new/reports folders are derived from the target instance.</summary>
public static class BatchEndpoints
{
    public static void MapBatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/batch", async (
            SubmitBatchRequest request,
            IBranchStore branches,
            IInstanceStore instances,
            INetworkShareResolver shareResolver,
            IJobSubmissionService submission,
            CancellationToken ct) =>
        {
            var scope = request.Scope;
            if (!StorageKeyValidator.IsValidKey(scope.BranchKey) || !StorageKeyValidator.IsValidKey(scope.InstanceKey))
                return Results.Problem("Invalid branchKey or instanceKey.", statusCode: StatusCodes.Status400BadRequest);

            var branch = await branches.GetByKeyAsync(scope.BranchKey, ct);
            if (branch is null)
                return Results.Problem($"Branch '{scope.BranchKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            var instance = await instances.GetByKeyAsync(branch.Id, scope.InstanceKey, ct);
            if (instance is null)
                return Results.Problem($"Instance '{scope.InstanceKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            // Resolve the instance's base path (share alias / credential profile) once,
            // then derive the conventional old/new/reports subfolders from it.
            ResolvedFolder baseResolved;
            try
            {
                baseResolved = shareResolver.Resolve(instance.BasePath, inlineCredentials: null, credentialProfile: instance.CredentialProfile);
            }
            catch (NetworkConfigurationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            string basePath = baseResolved.Path;
            string oldFolder = CombineFolder(basePath, "old");
            string newFolder = CombineFolder(basePath, "new");
            string reportsFolder = CombineFolder(basePath, "reports");

            // Authenticated UNC shares are validated in the worker; only check plain local paths up front.
            if (baseResolved.Credentials is null && !UncPath.IsUnc(oldFolder) && !Directory.Exists(oldFolder))
                return Results.Problem($"Old folder not found: {oldFolder}", statusCode: StatusCodes.Status400BadRequest);
            if (baseResolved.Credentials is null && !UncPath.IsUnc(newFolder) && !Directory.Exists(newFolder))
                return Results.Problem($"New folder not found: {newFolder}", statusCode: StatusCodes.Status400BadRequest);

            var job = new ComparisonJob
            {
                Id = Guid.NewGuid(),
                Request = new BatchComparisonRequest
                {
                    Scope = scope,
                    OldFolder = oldFolder,
                    NewFolder = newFolder,
                    ReportsFolder = reportsFolder,
                    SearchPattern = request.SearchPattern,
                    Recursive = request.Recursive,
                    Options = request.Options,
                    MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
                    Gate = request.Gate,
                    OldFolderCredentials = baseResolved.Credentials,
                    NewFolderCredentials = baseResolved.Credentials,
                },
                BranchId = branch.Id,
                InstanceId = instance.Id,
            };

            await submission.SubmitAsync(job, new RunBatchComparison(job.Id, scope.BranchKey, scope.InstanceKey), ct);

            return Results.Accepted($"/api/v1/jobs/{job.Id}", JobSummary.From(job));
        })
        .WithTags("Batch")
        .WithSummary("Submit a folder comparison job (folders derived from the instance)")
        .Produces<JobSummary>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Joins a base folder with a subfolder, preserving UNC separators for UNC roots.</summary>
    private static string CombineFolder(string root, string sub) =>
        UncPath.IsUnc(root) ? $@"{root.TrimEnd('\\', '/')}\{sub}" : Path.Combine(root, sub);
}
