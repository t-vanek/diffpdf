using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Pushes a persisted <see cref="SystemEvent"/> to the global <c>"scope"</c> group as a
/// <c>"systemEvent"</c> message — the realtime feed behind the client's notification center. Delivery is
/// fire-and-forget: a client that misses a push replays from the REST cursor (<c>GET /api/v1/events?sinceSeq</c>)
/// on its next reconnect, so nothing is lost permanently.
/// </summary>
public sealed class SignalRSystemEventPublisher(IHubContext<JobsHub> hub) : ISystemEventPublisher
{
    public Task PublishAsync(SystemEvent evt, CancellationToken ct = default) =>
        hub.Clients.Group("scope").SendAsync("systemEvent", evt, ct);
}
