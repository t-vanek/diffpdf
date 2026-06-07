using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Fans a <see cref="TriggerEvent"/> out to the trigger, job, instance and branch groups as a
/// <c>"triggerEvent"</c> message, so clients subscribed to any relevant scope receive it. Branch/instance
/// CRUD events also go to the global <c>"scope"</c> group (see <see cref="JobsHub.JoinScope"/>).
/// </summary>
public sealed class SignalRTriggerEventPublisher(IHubContext<JobsHub> hub) : ITriggerEventPublisher
{
    public Task PublishAsync(TriggerEvent evt, CancellationToken ct = default)
    {
        var sends = new List<Task>(5)
        {
            hub.Clients.Group($"trigger:{evt.TriggerId}").SendAsync("triggerEvent", evt, ct),
        };
        if (evt.BatchJobId is { } jobId)
            sends.Add(hub.Clients.Group($"job:{jobId}").SendAsync("triggerEvent", evt, ct));
        if (!string.IsNullOrEmpty(evt.BranchKey))
        {
            if (!string.IsNullOrEmpty(evt.InstanceKey))
                sends.Add(hub.Clients.Group($"instance:{evt.BranchKey}:{evt.InstanceKey}").SendAsync("triggerEvent", evt, ct));
            sends.Add(hub.Clients.Group($"branch:{evt.BranchKey}").SendAsync("triggerEvent", evt, ct));
        }
        // Scope CRUD also goes to the global "scope" group, so a client hears about branches/instances it has
        // not individually joined (e.g. a branch created elsewhere). Job lifecycle is delivered to the Jobs list
        // via jobProgress (which fires for every job, any source); other event types stay scoped to avoid noise.
        if (evt.EventType.StartsWith("branch.", StringComparison.Ordinal) ||
            evt.EventType.StartsWith("instance.", StringComparison.Ordinal))
            sends.Add(hub.Clients.Group("scope").SendAsync("triggerEvent", evt, ct));

        return Task.WhenAll(sends);
    }
}
