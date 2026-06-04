using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Fans a <see cref="TriggerEvent"/> out to the trigger, job, instance and branch groups as a
/// <c>"triggerEvent"</c> message, so clients subscribed to any relevant scope receive it.
/// </summary>
public sealed class SignalRTriggerEventPublisher(IHubContext<JobsHub> hub) : ITriggerEventPublisher
{
    public Task PublishAsync(TriggerEvent evt, CancellationToken ct = default)
    {
        var sends = new List<Task>(4)
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
        return Task.WhenAll(sends);
    }
}
