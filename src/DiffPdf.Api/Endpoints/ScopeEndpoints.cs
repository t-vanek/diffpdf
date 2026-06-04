using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Branch and instance management endpoints.</summary>
public static class ScopeEndpoints
{
    public static void MapScopeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/branches").WithTags("Scope");

        group.MapPost("/", async (
            CreateBranchRequest request, IBranchStore store, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid branch key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var created = await store.CreateAsync(request.Key, request.Name, ct);
                return Results.Created($"/api/v1/branches/{created.Key}", created);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Create a branch").Produces<Branch>(StatusCodes.Status201Created)
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (IBranchStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)))
            .WithSummary("List branches").Produces<IReadOnlyList<Branch>>();

        group.MapGet("/{branchKey}", async (
            string branchKey, IBranchStore store, CancellationToken ct) =>
        {
            var branch = await store.GetByKeyAsync(branchKey, ct);
            return branch is null ? Results.NotFound() : Results.Ok(branch);
        }).WithSummary("Get a branch").Produces<Branch>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{branchKey}", async (
            string branchKey, IBranchStore branches, IInstanceStore instances, IJobStore jobs, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null) return Results.NotFound();

            // Guard 1: a branch that still holds instances must not be deleted (delete the instances first).
            var branchInstances = await instances.ListAsync(branch.Id, ct);
            if (branchInstances.Count > 0)
                return Results.Problem(
                    $"Branch '{branchKey}' has {branchInstances.Count} instance(s); delete those first.",
                    statusCode: StatusCodes.Status409Conflict);

            // Guard 2: do not delete while a job for this branch is still active.
            if (await HasActiveJobsAsync(jobs, branchKey, ct))
                return Results.Problem(
                    $"Branch '{branchKey}' has an active job (Draft/Queued/Running/Paused); cancel or wait for it first.",
                    statusCode: StatusCodes.Status409Conflict);

            await branches.DeleteByKeyAsync(branchKey, ct);
            return Results.NoContent();
        }).WithSummary("Delete a branch (409 if it has instances or an active job)")
          .Produces(StatusCodes.Status204NoContent)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{branchKey}/instances", async (
            string branchKey, CreateInstanceRequest request,
            IBranchStore branches, IInstanceStore instances, IInstanceStructureService structure,
            bool? ensureStructure, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid instance key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(request.BasePath))
                return Results.Problem("Instance basePath must not be empty.", statusCode: StatusCodes.Status400BadRequest);

            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null)
                return Results.Problem($"Branch '{branchKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            ComparisonInstance created;
            try
            {
                created = await instances.CreateAsync(
                    branch.Id, request.Key, request.Name, request.BasePath, request.CredentialProfile, ct);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            // Provision old/new/reports under the base path by default; best-effort so an
            // unreachable share does not undo the (valid) instance record.
            InstanceStructureReport? report = null;
            if (ensureStructure != false)
                report = await structure.EnsureAsync(created.BasePath, created.CredentialProfile, ct: ct);

            return Results.Created(
                $"/api/v1/branches/{branchKey}/instances/{created.Key}",
                new CreatedInstanceResponse(created, report));
        }).WithSummary("Create an instance under a branch (provisions old/new/reports unless ?ensureStructure=false)")
          .Produces<CreatedInstanceResponse>(StatusCodes.Status201Created)
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{branchKey}/instances", async (
            string branchKey, IBranchStore branches, IInstanceStore instances, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null) return Results.NotFound();
            return Results.Ok(await instances.ListAsync(branch.Id, ct));
        }).WithSummary("List instances").Produces<IReadOnlyList<ComparisonInstance>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{branchKey}/instances/{instanceKey}", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null) return Results.NotFound();
            var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        }).WithSummary("Get an instance").Produces<ComparisonInstance>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{branchKey}/instances/{instanceKey}", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances,
            IJobStore jobs, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null) return Results.NotFound();
            var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
            if (instance is null) return Results.NotFound();

            // Guard against orphaning the instance's jobs (active or historical).
            var instanceJobs = await jobs.ListAsync(new JobListQuery { BranchKey = branchKey, InstanceKey = instanceKey }, ct);
            if (instanceJobs.Any(j => ActiveStatuses.Contains(j.Status)))
                return Results.Problem($"Instance '{instanceKey}' has an active job; cancel or wait for it first.", statusCode: StatusCodes.Status409Conflict);
            if (instanceJobs.Count > 0)
                return Results.Problem($"Instance '{instanceKey}' has job history; it cannot be deleted (history is preserved).", statusCode: StatusCodes.Status409Conflict);

            await instances.DeleteByKeyAsync(branch.Id, instanceKey, ct);
            return Results.NoContent();
        }).WithSummary("Delete an instance (409 if it has any jobs)")
          .Produces(StatusCodes.Status204NoContent)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{branchKey}/instances/{instanceKey}/structure", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, IInstanceStructureService structure,
            bool? includeFiles, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(branchKey, instanceKey, branches, instances, ct);
            return instance is null
                ? Results.NotFound()
                : Results.Ok(await structure.EnsureAsync(instance.BasePath, instance.CredentialProfile, includeFiles ?? false, ct));
        }).WithSummary("Create/repair the structure (missing -> created, file collision -> replaced); reports old/new PDF content")
          .Produces<InstanceStructureReport>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{branchKey}/instances/{instanceKey}/readiness", async (
            string branchKey, string instanceKey, int? sampleSize, bool? includeFiles,
            IBranchStore branches, IInstanceStore instances, IInstanceStructureService structure,
            INetworkShareResolver shareResolver, INetworkDiscoveryService discovery, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();

            // 1) Inspect the skeleton: reachability, per-subfolder state and old/new PDF counts.
            var report = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, includeFiles ?? false, ct);
            if (!report.Reachable)
                return Results.Ok(new InstanceReadiness(report, 0, 0, 0, [], [], false, report.Error));

            // 2) Derive the concrete old/new folders from the instance base (resolving a
            //    share alias / profile once), then dry-run the pairing to see what lines up.
            string oldFolder, newFolder;
            try
            {
                string basePath = shareResolver.Resolve(instance.BasePath, inlineCredentials: null, credentialProfile: instance.CredentialProfile).Path;
                oldFolder = InstanceFolders.Old(basePath);
                newFolder = InstanceFolders.New(basePath);
            }
            catch (NetworkConfigurationException ex)
            {
                return Results.Ok(new InstanceReadiness(report, 0, 0, 0, [], [], false, ex.Message));
            }

            var pairing = await discovery.PreviewPairingAsync(
                oldFolder, newFolder,
                oldInlineCredentials: null, oldCredentialProfile: instance.CredentialProfile,
                newInlineCredentials: null, newCredentialProfile: instance.CredentialProfile,
                searchPattern: "*.pdf", recursive: true, sampleSize: sampleSize ?? 20, ct: ct);

            return Results.Ok(new InstanceReadiness(
                report, pairing.Matched, pairing.OnlyInOld, pairing.OnlyInNew,
                pairing.SampleOnlyInOld, pairing.SampleOnlyInNew,
                report.HasComparableInputs, pairing.Error));
        }).WithSummary("Batch readiness: folder-skeleton state + old/new PDF counts, how they pair up, and whether a job may be submitted (?includeFiles=true returns the full file list)")
          .Produces<InstanceReadiness>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<ComparisonInstance?> ResolveInstanceAsync(
        string branchKey, string instanceKey, IBranchStore branches, IInstanceStore instances, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        return await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
    }

    private static readonly JobStatus[] ActiveStatuses =
        [JobStatus.Draft, JobStatus.Queued, JobStatus.Running, JobStatus.Paused];

    private static async Task<bool> HasActiveJobsAsync(IJobStore jobs, string branchKey, CancellationToken ct)
    {
        foreach (var status in ActiveStatuses)
        {
            var list = await jobs.ListAsync(new JobListQuery { BranchKey = branchKey, Status = status }, ct);
            if (list.Count > 0) return true;
        }
        return false;
    }
}
