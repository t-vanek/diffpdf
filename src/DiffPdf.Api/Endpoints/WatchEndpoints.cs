using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed folder-watch — one per instance. Setting it arms the DB-backed watcher (picked
/// up within a tick, no restart); the watcher launches a batch when a drop into the instance's
/// <c>new/</c> folder settles. Managed entirely via the API, so no server access is needed.
/// </summary>
public static class WatchEndpoints
{
    public static void MapWatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/branches/{branchKey}/instances/{instanceKey}").WithTags("Watches");

        group.MapPut("/watch", async (
            string branchKey, string instanceKey, SetWatchRequest request,
            IBranchStore branches, IInstanceStore instances, IWatchStore watches, CancellationToken ct) =>
        {
            var (branch, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (branch is null || instance is null) return Results.NotFound();

            var watch = new FolderWatch
            {
                Id = Guid.NewGuid(),
                BranchId = branch.Id,
                InstanceId = instance.Id,
                BranchKey = branch.Key,
                InstanceKey = instance.Key,
                StabilitySeconds = request.StabilitySeconds,
                Enabled = request.Enabled,
            };
            var saved = await watches.UpsertAsync(watch, ct);
            return Results.Ok(WatchResponse.From(saved));
        }).WithSummary("Create or replace the instance's folder-watch (arms the watcher)")
          .Produces<WatchResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/watch", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, IWatchStore watches, CancellationToken ct) =>
        {
            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            var watch = await watches.GetByInstanceAsync(instance.Id, ct);
            return watch is null ? Results.NotFound() : Results.Ok(WatchResponse.From(watch));
        }).WithSummary("Get the instance's folder-watch").Produces<WatchResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/watch", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, IWatchStore watches, CancellationToken ct) =>
        {
            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            return await watches.DeleteByInstanceAsync(instance.Id, ct) ? Results.NoContent() : Results.NotFound();
        }).WithSummary("Remove the instance's folder-watch").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/watches", async (IWatchStore watches, CancellationToken ct) =>
            Results.Ok((await watches.ListAsync(ct)).Select(WatchResponse.From)))
            .WithTags("Watches").WithSummary("List all folder-watches").Produces<IEnumerable<WatchResponse>>();
    }

    private static async Task<(Branch? Branch, ComparisonInstance? Instance)> ResolveAsync(
        string branchKey, string instanceKey, IBranchStore branches, IInstanceStore instances, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return (null, null);
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        return (branch, instance);
    }
}
