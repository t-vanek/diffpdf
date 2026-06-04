using DiffPdf.Api;
using DiffPdf.Messaging.ScopeSync;
using Microsoft.Extensions.Options;

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

        app.MapGet("/scope/root", (IOptions<ScopeSyncOptions> options) =>
        {
            string? root = string.IsNullOrWhiteSpace(options.Value.RootPath) ? null : options.Value.RootPath!.Trim();
            return Results.Ok(new ScopeRootInfo(root, root is not null));
        }).WithTags("Scope")
          .WithSummary("The configured structure root (ScopeSync:RootPath) under which branches/instances are created (null when not configured). Branch/instance paths are derived from it server-side.")
          .Produces<ScopeRootInfo>();
    }
}
