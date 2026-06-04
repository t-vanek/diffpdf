using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Messaging.Observability;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Messaging.Triggers;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Endpoints;

/// <summary>Branch and instance management endpoints.</summary>
public static class ScopeEndpoints
{
    public static void MapScopeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/branches").WithTags("Scope");

        group.MapPost("/", async (
            CreateBranchRequest request, IBranchStore store, IControlCheckProvisioner provisioner,
            IScopeConfigurationProvisioner configProvisioner,
            IInstanceStructureService structure, IOptions<ScopeSyncOptions> scopeSync,
            bool? autoProvision, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid branch key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);
            Branch created;
            try
            {
                created = await store.CreateAsync(request.Key, request.Name, ct);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            // When a structure root is configured, create the branch folder <root>/<branch> on disk so the
            // tree is laid out top-down. Best-effort: an unreachable root does not undo the branch record.
            string root = (scopeSync.Value.RootPath ?? "").Trim();
            if (root.Length > 0)
                await structure.EnsureFolderAsync(InstanceFolders.Combine(root, created.Key), scopeSync.Value.CredentialProfile, ct);

            // Provision the branch's readiness check (covers every instance under it). Idempotent and
            // best-effort so it never fails the create; suppressed with ?autoProvision=false.
            if (autoProvision != false)
            {
                await provisioner.EnsureBranchChecksAsync(created.Key, ct);
                await configProvisioner.EnsureBranchConfigAsync(created.Id, ct);
            }

            return Results.Created($"/api/v1/branches/{created.Key}", created);
        }).WithSummary("Create a branch (also creates <root>/<branch> when a structure root is configured, and its readiness check unless ?autoProvision=false)")
          .Produces<Branch>(StatusCodes.Status201Created)
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

        group.MapPut("/{branchKey}", async (
            string branchKey, UpdateBranchRequest request, IBranchStore store, ITriggerEventPublisher events, CancellationToken ct) =>
        {
            var existing = await store.GetByKeyAsync(branchKey, ct);
            if (existing is null) return Results.NotFound();

            var updated = existing with
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name,
                Enabled = request.Enabled,
            };
            try
            {
                var saved = await store.UpdateAsync(updated, request.Version, ct);
                await events.PublishAsync(new TriggerEvent("branch.status.changed", Guid.Empty,
                    BranchId: saved.Id, BranchKey: saved.Key, Status: saved.Enabled ? "Enabled" : "Disabled"), ct);
                return Results.Ok(saved);
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update a branch's name/enabled (optimistic concurrency via Version)")
          .Produces<Branch>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{branchKey}/stats", async (
            string branchKey, IBranchStore branches, ScopeStatsService stats, CancellationToken ct) =>
        {
            if (await branches.GetByKeyAsync(branchKey, ct) is null) return Results.NotFound();
            return Results.Ok(await stats.ForBranchAsync(branchKey, ct));
        }).WithSummary("Aggregated statistics for a branch (jobs, checks, automations, comparisons)")
          .Produces<ScopeStatsResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{branchKey}", async (
            string branchKey, IBranchStore branches, IInstanceStore instances, IJobStore jobs,
            IControlCheckProvisioner provisioner, IScopeConfigurationProvisioner configProvisioner,
            BranchDispatchLocks dispatchLocks, DiffPdfMetrics metrics, CancellationToken ct) =>
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
            // Remove the auto-provisioned readiness check, the branch config and the queue lock so nothing outlives the branch.
            await provisioner.RemoveBranchChecksAsync(branchKey, ct);
            await configProvisioner.RemoveBranchConfigAsync(branch.Id, ct);
            dispatchLocks.Remove(branch.Id);
            metrics.RemoveBranch(branch.Id);
            return Results.NoContent();
        }).WithSummary("Delete a branch (409 if it has instances or an active job)")
          .Produces(StatusCodes.Status204NoContent)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{branchKey}/instances", async (
            string branchKey, CreateInstanceRequest request,
            IBranchStore branches, IInstanceStore instances, IInstanceStructureService structure,
            IControlCheckProvisioner provisioner, ITriggerProvisioner triggerProvisioner,
            IScopeConfigurationProvisioner configProvisioner, IOptions<ScopeSyncOptions> scopeSync,
            bool? ensureStructure, bool? autoProvision, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid instance key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);

            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null)
                return Results.Problem($"Branch '{branchKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            // When a structure root is configured, the base path is derived as <root>/<branch>/<instance>
            // (the caller's basePath is ignored). Otherwise the caller must supply an explicit base path.
            string root = (scopeSync.Value.RootPath ?? "").Trim();
            bool useRoot = root.Length > 0;
            if (!useRoot && string.IsNullOrWhiteSpace(request.BasePath))
                return Results.Problem("Instance basePath must not be empty (no ScopeSync root is configured).", statusCode: StatusCodes.Status400BadRequest);

            string basePath = useRoot
                ? InstanceFolders.Combine(InstanceFolders.Combine(root, branchKey), request.Key)
                : request.BasePath;
            // Under a shared root the instance inherits the root's credential profile unless one is supplied.
            string? credentialProfile = useRoot && string.IsNullOrWhiteSpace(request.CredentialProfile)
                ? scopeSync.Value.CredentialProfile
                : request.CredentialProfile;

            ComparisonInstance created;
            try
            {
                created = await instances.CreateAsync(
                    branch.Id, request.Key, request.Name, basePath, credentialProfile, ct);
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

            // Ensure the branch's readiness check exists (it already covers this new instance). Idempotent
            // safety net for branches that predate the branch-create hook; suppressed with ?autoProvision=false.
            if (autoProvision != false)
            {
                await provisioner.EnsureBranchChecksAsync(branchKey, ct);
                // After a correct branch+instance creation, provision the instance's default trigger (idempotent).
                await triggerProvisioner.EnsureDefaultTriggerAsync(branch.Id, branchKey, created.Id, created.Key, actor: null, ct);
                // Provision the instance's config row (defaults to inheriting from the branch).
                await configProvisioner.EnsureInstanceConfigAsync(branch.Id, created.Id, ct);
            }

            return Results.Created(
                $"/api/v1/branches/{branchKey}/instances/{created.Key}",
                new CreatedInstanceResponse(created, report));
        }).WithSummary("Create an instance under a branch (provisions old/new/reports unless ?ensureStructure=false; ensures the branch readiness check unless ?autoProvision=false)")
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

        group.MapPut("/{branchKey}/instances/{instanceKey}", async (
            string branchKey, string instanceKey, UpdateInstanceRequest request,
            IBranchStore branches, IInstanceStore instances, ITriggerEventPublisher events, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.BasePath))
                return Results.Problem("Instance basePath must not be empty.", statusCode: StatusCodes.Status400BadRequest);

            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null) return Results.NotFound();
            var existing = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
            if (existing is null) return Results.NotFound();

            var updated = existing with
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name,
                BasePath = request.BasePath,
                CredentialProfile = string.IsNullOrWhiteSpace(request.CredentialProfile) ? null : request.CredentialProfile,
                Enabled = request.Enabled,
            };
            try
            {
                var saved = await instances.UpdateAsync(updated, request.Version, ct);
                await events.PublishAsync(new TriggerEvent("instance.status.changed", Guid.Empty,
                    BranchId: branch.Id, InstanceId: saved.Id, BranchKey: branchKey, InstanceKey: saved.Key,
                    Status: saved.Enabled ? "Enabled" : "Disabled"), ct);
                return Results.Ok(saved);
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update an instance's name/basePath/credentialProfile/enabled (optimistic concurrency via Version)")
          .Produces<ComparisonInstance>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{branchKey}/instances/{instanceKey}", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances,
            IJobStore jobs, ITriggerProvisioner triggerProvisioner,
            IScopeConfigurationProvisioner configProvisioner, CancellationToken ct) =>
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
            // Soft-delete the instance's triggers (run history is preserved) and remove its config row.
            await triggerProvisioner.SoftDeleteInstanceTriggersAsync(instance.Id, actor: null, ct);
            await configProvisioner.RemoveInstanceConfigAsync(instance.Id, ct);
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

        group.MapGet("/{branchKey}/instances/{instanceKey}/stats", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, ScopeStatsService stats, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            return Results.Ok(await stats.ForInstanceAsync(branchKey, instanceKey, ct));
        }).WithSummary("Aggregated statistics for an instance (jobs, checks, automations, comparisons)")
          .Produces<ScopeStatsResponse>().ProducesProblem(StatusCodes.Status404NotFound);
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
