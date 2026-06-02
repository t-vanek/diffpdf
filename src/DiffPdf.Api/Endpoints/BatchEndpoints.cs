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
            IInstanceStructureService structure,
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

            // Pre-flight: the structure must be reachable and both input folders must hold
            // PDFs, otherwise the job would run with nothing to compare. Inspecting also
            // covers existence (a missing old/new yields a null PdfCount, caught below).
            var structureReport = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, ct: ct);
            if (!structureReport.Reachable)
                return Results.Problem(
                    $"Instance base path is not reachable: {structureReport.Error}", statusCode: StatusCodes.Status400BadRequest);

            int oldPdfs = structureReport.Items.FirstOrDefault(i => i.Name == "old")?.PdfCount ?? 0;
            int newPdfs = structureReport.Items.FirstOrDefault(i => i.Name == "new")?.PdfCount ?? 0;
            if (oldPdfs == 0 || newPdfs == 0)
                return Results.Problem(
                    $"Nothing to compare: 'old' has {oldPdfs} PDF(s), 'new' has {newPdfs}. Both folders must contain at least one PDF.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            // Resolve the instance's base path (share alias / credential profile) for the
            // durable job, then derive the conventional old/new/reports subfolders.
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
            string oldFolder = InstanceFolders.Old(basePath);
            string newFolder = InstanceFolders.New(basePath);
            string reportsFolder = InstanceFolders.Reports(basePath);

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
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
