using DiffPdf.Api.Operational;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Operational status dashboard (authenticated). Surfaces the running automation so it can be
/// observed entirely over the API — no RDP: leader + lease, per-service heartbeats, queue backlog,
/// enabled schedules/watches, and dependency health.
/// </summary>
public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/status", async (OperationalStatusService status, CancellationToken ct) =>
            Results.Ok(await status.BuildStatusAsync(ct)))
            .WithTags("Operations")
            .WithSummary("Operational status: leader, service heartbeats, backlog, dependencies")
            .Produces<OperationalStatusResponse>();
    }
}
