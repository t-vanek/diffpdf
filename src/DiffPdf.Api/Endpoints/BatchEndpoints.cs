using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Creates a batch comparison job in Draft. The job is started separately via POST /jobs/{id}/start.</summary>
public static class BatchEndpoints
{
    public static void MapBatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/batch", async (
            SubmitBatchRequest request,
            IBranchStore branches,
            IInstanceStore instances,
            INetworkShareResolver shareResolver,
            IJobStore jobStore,
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

            // Resolve the instance's base path (share alias / credential profile) and derive the
            // conventional old/new/reports subfolders. The job is created in Draft and does not run
            // until POST /jobs/{id}/start — where the "is there anything to compare" check happens
            // (PDFs may be added between creating and starting the job).
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

            var job = new ComparisonJob
            {
                Id = Guid.NewGuid(),
                Status = JobStatus.Draft,
                Request = new BatchComparisonRequest
                {
                    Scope = scope,
                    OldFolder = InstanceFolders.Old(basePath),
                    NewFolder = InstanceFolders.New(basePath),
                    ReportsFolder = InstanceFolders.Reports(basePath),
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

            var created = await jobStore.CreateAsync(job, ct);
            return Results.Created($"/api/v1/jobs/{created.Id}", JobSummary.From(created));
        })
        .WithTags("Batch")
        .WithSummary("Create a folder comparison job in Draft (start it with POST /jobs/{id}/start)")
        .Produces<JobSummary>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
