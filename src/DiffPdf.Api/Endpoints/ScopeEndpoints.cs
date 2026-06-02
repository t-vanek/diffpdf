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
                report = await structure.EnsureAsync(created.BasePath, created.CredentialProfile, ct);

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

        group.MapGet("/{branchKey}/instances/{instanceKey}/structure", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, IInstanceStructureService structure, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(branchKey, instanceKey, branches, instances, ct);
            return instance is null
                ? Results.NotFound()
                : Results.Ok(await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, ct));
        }).WithSummary("Inspect the instance's old/new/reports structure")
          .Produces<InstanceStructureReport>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{branchKey}/instances/{instanceKey}/structure", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, IInstanceStructureService structure, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(branchKey, instanceKey, branches, instances, ct);
            return instance is null
                ? Results.NotFound()
                : Results.Ok(await structure.EnsureAsync(instance.BasePath, instance.CredentialProfile, ct));
        }).WithSummary("Create/repair the instance's old/new/reports structure (missing -> created, file collision -> replaced)")
          .Produces<InstanceStructureReport>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<ComparisonInstance?> ResolveInstanceAsync(
        string branchKey, string instanceKey, IBranchStore branches, IInstanceStore instances, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        return await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
    }
}
