using DiffPdf.Api;
using DiffPdf.Application.Scope;
using DiffPdf.Application.Stats;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Persistence;
using System.Security.Claims;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Branch and instance management endpoints. The handlers only bind the request, call the application
/// services (<see cref="IBranchService"/> / <see cref="IInstanceService"/>) and map the outcome to HTTP —
/// all validation, provisioning, audit and realtime events live in DiffPdf.Application.
/// </summary>
public static class ScopeEndpoints
{
    public static void MapScopeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/branches").WithTags("Scope");

        group.MapPost("/", (
            CreateBranchRequest request, IBranchService branches, ClaimsPrincipal user,
            bool? autoProvision, CancellationToken ct) =>
            Run(async () =>
            {
                var created = await branches.CreateAsync(
                    new CreateBranchInput(request.Key, request.Name), autoProvision != false, user.Actor(), ct);
                return Results.Created($"/api/v1/branches/{created.Key}", created);
            }))
            .WithSummary("Create a branch (also creates <root>/<branch> when a structure root is configured, and its readiness check unless ?autoProvision=false)")
            .Produces<Branch>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (IBranchService branches, CancellationToken ct) =>
            Results.Ok(await branches.ListAsync(ct)))
            .WithSummary("List branches").Produces<IReadOnlyList<Branch>>();

        // One call returns every branch with its instance count + run-queue state, so the desktop list does not
        // make a per-branch queue round-trip (N+1). The batching lives in the service (a few queries total).
        group.MapGet("/summary", async (IBranchService branches, CancellationToken ct) =>
            Results.Ok((await branches.ListSummariesAsync(ct))
                .Select(v => new BranchSummary(v.Branch, v.InstanceCount, v.Queue))))
            .WithSummary("List branches with instance count + run-queue state in a few batch queries (no per-branch round-trips).")
            .Produces<IReadOnlyList<BranchSummary>>();

        group.MapGet("/{branchKey}", async (string branchKey, IBranchService branches, CancellationToken ct) =>
            await branches.GetByKeyAsync(branchKey, ct) is { } branch ? Results.Ok(branch) : Results.NotFound())
            .WithSummary("Get a branch").Produces<Branch>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{branchKey}", (
            string branchKey, UpdateBranchRequest request, IBranchService branches, ClaimsPrincipal user, CancellationToken ct) =>
            Run(async () =>
            {
                var saved = await branches.UpdateAsync(
                    branchKey, new UpdateBranchInput(request.Name, request.Enabled, request.Version), user.Actor(), ct);
                return saved is null ? Results.NotFound() : Results.Ok(saved);
            }))
            .WithSummary("Update a branch's name/enabled (optimistic concurrency via Version)")
            .Produces<Branch>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{branchKey}/stats", async (
            string branchKey, IBranchService branches, IScopeStatsService stats, CancellationToken ct) =>
        {
            if (await branches.GetByKeyAsync(branchKey, ct) is null) return Results.NotFound();
            return Results.Ok(await stats.ForBranchAsync(branchKey, ct));
        }).WithSummary("Aggregated statistics for a branch (jobs, checks, automations, comparisons)")
          .Produces<ScopeStatsResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{branchKey}/audit", async (
            string branchKey, IBranchService branches, IAuditLogStore audit, int? limit, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null) return Results.NotFound();
            var entries = await audit.ListAsync("Branch", branch.Id.ToString(), limit ?? 100, ct);
            return Results.Ok(entries.Select(AuditEntryResponse.From).ToList());
        }).WithSummary("List a branch's audit entries (created / updated / deleted).")
          .Produces<IReadOnlyList<AuditEntryResponse>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{branchKey}", (
            string branchKey, bool? cascade, IBranchService branches, ClaimsPrincipal user, CancellationToken ct) =>
            Run(async () =>
            {
                var deleted = await branches.DeleteAsync(branchKey, cascade == true, user.Actor(), ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }))
            .WithSummary("Delete a branch. Without ?cascade=true, 409 if it has instances or an active job; with cascade, also deletes its instances + jobs.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{branchKey}/instances", (
            string branchKey, CreateInstanceRequest request, IInstanceService instances, ClaimsPrincipal user,
            bool? ensureStructure, bool? autoProvision, CancellationToken ct) =>
            Run(async () =>
            {
                var result = await instances.CreateAsync(
                    branchKey,
                    new CreateInstanceInput(request.Key, request.Name, request.BasePath, request.CredentialProfile),
                    ensureStructure != false, autoProvision != false, user.Actor(), ct);
                return Results.Created(
                    $"/api/v1/branches/{branchKey}/instances/{result.Instance.Key}",
                    new CreatedInstanceResponse(result.Instance, result.Structure));
            }))
            .WithSummary("Create an instance under a branch (provisions old/new/reports unless ?ensureStructure=false; ensures the branch readiness check unless ?autoProvision=false)")
            .Produces<CreatedInstanceResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{branchKey}/instances", async (string branchKey, IInstanceService instances, CancellationToken ct) =>
            await instances.ListAsync(branchKey, ct) is { } list ? Results.Ok(list) : Results.NotFound())
            .WithSummary("List instances").Produces<IReadOnlyList<ComparisonInstance>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{branchKey}/instances/{instanceKey}", async (
            string branchKey, string instanceKey, IInstanceService instances, CancellationToken ct) =>
            await instances.GetAsync(branchKey, instanceKey, ct) is { } instance ? Results.Ok(instance) : Results.NotFound())
            .WithSummary("Get an instance").Produces<ComparisonInstance>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{branchKey}/instances/{instanceKey}", (
            string branchKey, string instanceKey, UpdateInstanceRequest request, IInstanceService instances,
            ClaimsPrincipal user, CancellationToken ct) =>
            Run(async () =>
            {
                var saved = await instances.UpdateAsync(
                    branchKey, instanceKey,
                    new UpdateInstanceInput(request.Name, request.BasePath, request.CredentialProfile, request.Enabled, request.Version),
                    user.Actor(), ct);
                return saved is null ? Results.NotFound() : Results.Ok(saved);
            }))
            .WithSummary("Update an instance's name/basePath/credentialProfile/enabled (optimistic concurrency via Version)")
            .Produces<ComparisonInstance>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{branchKey}/instances/{instanceKey}", (
            string branchKey, string instanceKey, IInstanceService instances, ClaimsPrincipal user, CancellationToken ct) =>
            Run(async () =>
            {
                var deleted = await instances.DeleteAsync(branchKey, instanceKey, user.Actor(), ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }))
            .WithSummary("Delete an instance (409 if it has any jobs)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{branchKey}/instances/{instanceKey}/structure", async (
            string branchKey, string instanceKey, IInstanceService instances, bool? includeFiles, CancellationToken ct) =>
            await instances.EnsureStructureAsync(branchKey, instanceKey, includeFiles ?? false, ct) is { } report
                ? Results.Ok(report)
                : Results.NotFound())
            .WithSummary("Create/repair the structure (missing -> created, file collision -> replaced); reports old/new PDF content")
            .Produces<InstanceStructureReport>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{branchKey}/instances/{instanceKey}/readiness", async (
            string branchKey, string instanceKey, int? sampleSize, bool? includeFiles,
            IInstanceService instances, CancellationToken ct) =>
        {
            var r = await instances.GetReadinessAsync(branchKey, instanceKey, sampleSize, includeFiles ?? false, ct);
            return r is null
                ? Results.NotFound()
                : Results.Ok(new InstanceReadiness(
                    r.Structure, r.Matched, r.OnlyInOld, r.OnlyInNew,
                    r.SampleOnlyInOld, r.SampleOnlyInNew, r.Ready, r.Error));
        }).WithSummary("Batch readiness: folder-skeleton state + old/new PDF counts, how they pair up, and whether a job may be submitted (?includeFiles=true returns the full file list)")
          .Produces<InstanceReadiness>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{branchKey}/instances/{instanceKey}/stats", async (
            string branchKey, string instanceKey, IInstanceService instances, IScopeStatsService stats, CancellationToken ct) =>
        {
            if (await instances.GetAsync(branchKey, instanceKey, ct) is null) return Results.NotFound();
            return Results.Ok(await stats.ForInstanceAsync(branchKey, instanceKey, ct));
        }).WithSummary("Aggregated statistics for an instance (jobs, checks, automations, comparisons)")
          .Produces<ScopeStatsResponse>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Runs a write handler and maps the application layer's outcome to an HTTP status, preserving the
    /// endpoint's prior behaviour: validation → 400, business conflict → 409, referenced parent missing → 404.
    /// The services translate store conflicts (duplicate key / concurrency) into <see cref="ScopeConflictException"/>,
    /// so the endpoint only deals with the Scope* family. (Entity-not-found on update/delete is signalled by a
    /// null/false return and handled inline, not here.)
    /// </summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ScopeValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (ScopeConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (ScopeNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
    }
}
