using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Read + re-send surface over the notification outbox: the delivery history (what was sent, what is
/// retrying, what dead-lettered and why) and a manual re-queue for a failed/dead-lettered row. The rows
/// themselves are appended by the dispatcher and sent by the background delivery service.
/// </summary>
public static class NotificationDeliveryEndpoints
{
    public static void MapNotificationDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/notifications/deliveries").WithTags("Notifications");

        group.MapGet("/", async (string? status, int? limit, INotificationDeliveryStore store, CancellationToken ct) =>
            {
                NotificationDeliveryStatus? filter = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    if (!Enum.TryParse<NotificationDeliveryStatus>(status, ignoreCase: true, out var parsed))
                        return Results.Problem($"Unknown status '{status}'.", statusCode: StatusCodes.Status400BadRequest);
                    filter = parsed;
                }
                int take = Math.Clamp(limit ?? 100, 1, 500);
                var rows = await store.ListRecentAsync(take, filter, ct);
                return Results.Ok(rows.Select(NotificationDeliveryResponse.From));
            })
            .WithSummary("List recent notification deliveries (newest first; optional status filter)")
            .Produces<IEnumerable<NotificationDeliveryResponse>>().ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/{id:guid}/resend", async (Guid id, INotificationDeliveryStore store, CancellationToken ct) =>
            {
                var row = await store.GetAsync(id, ct);
                if (row is null)
                    return Results.NotFound();
                if (row.Status == NotificationDeliveryStatus.Sent)
                    return Results.Problem("Tato notifikace už byla odeslána.", statusCode: StatusCodes.Status409Conflict);

                await store.RequeueAsync(id, DateTimeOffset.UtcNow, ct);
                return Results.Ok(NotificationDeliveryResponse.From((await store.GetAsync(id, ct))!));
            })
            .WithSummary("Re-queue a failed/dead-lettered delivery for sending now")
            .Produces<NotificationDeliveryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
    }
}
