using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Read surface over the append-only system event log. Two modes: <c>?sinceSeq=N</c> replays everything
/// newer than the cursor (oldest first) — how the client catches up after a reconnect so no event is ever
/// lost; without a cursor it returns the newest events (newest first) — the notification center's initial
/// fill. Realtime delivery happens via the SignalR <c>systemEvent</c> push; this endpoint is the durable
/// source of truth behind it.
/// </summary>
public static class SystemEventEndpoints
{
    public static void MapSystemEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events", async (long? sinceSeq, int? limit, string? minSeverity, ISystemEventStore store, CancellationToken ct) =>
            {
                SystemEventSeverity? floor = null;
                if (!string.IsNullOrWhiteSpace(minSeverity))
                {
                    if (!Enum.TryParse<SystemEventSeverity>(minSeverity, ignoreCase: true, out var parsed))
                        return Results.Problem($"Unknown severity '{minSeverity}'.", statusCode: StatusCodes.Status400BadRequest);
                    floor = parsed;
                }
                int take = Math.Clamp(limit ?? 200, 1, 1000);
                var rows = sinceSeq is { } cursor
                    ? await store.ListSinceAsync(cursor, take, floor, ct)
                    : await store.ListRecentAsync(take, ct);
                return Results.Ok(rows.Select(SystemEventResponse.From));
            })
            .WithTags("Events")
            .WithSummary("System event log: ?sinceSeq=N replays newer-than-cursor (oldest first); without it returns the newest (newest first)")
            .Produces<IEnumerable<SystemEventResponse>>().ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
