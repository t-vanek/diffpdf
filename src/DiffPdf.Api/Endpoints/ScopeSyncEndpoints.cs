using DiffPdf.Messaging.ScopeSync;

namespace DiffPdf.Api.Endpoints;

/// <summary>On-demand scope synchronization: reconcile the on-disk folder tree with the database.</summary>
public static class ScopeSyncEndpoints
{
    public static void MapScopeSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/scope/sync", async (bool? apply, IScopeSyncService sync, CancellationToken ct) =>
            Results.Ok(await sync.SynchronizeAsync(apply ?? false, ct)))
            .WithTags("Scope")
            .WithSummary("Detect the on-disk <root>/<branch>/<instance> structure and reconcile it with the database. Dry-run by default; ?apply=true registers branches/instances and creates missing folders.")
            .Produces<ScopeSyncReport>()
            .RequireRateLimiting("expensive");
    }
}
