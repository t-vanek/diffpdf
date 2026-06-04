using DiffPdf.Api.Auth;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Realtime job-progress + trigger-event notifications. Clients join a job, instance, branch or trigger
/// group and receive "jobProgress" / "triggerEvent" events. This is a notification channel only — REST
/// remains the source of truth, so a client that misses an event can reload via the API.
/// When auth is on the connection is gated to ≥ Viewer (see Program.cs); scope subscriptions are audited.
/// </summary>
public sealed class JobsHub(IAuditLogStore audit) : Hub
{
    public Task JoinJob(Guid jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task LeaveJob(Guid jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public async Task JoinInstance(string branchKey, string instanceKey)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"instance:{branchKey}:{instanceKey}");
        await AuditSubscribeAsync("Instance", $"{branchKey}/{instanceKey}");
    }

    public async Task JoinBranch(string branchKey)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"branch:{branchKey}");
        await AuditSubscribeAsync("Branch", branchKey);
    }

    public async Task JoinTrigger(Guid triggerId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"trigger:{triggerId}");
        await AuditSubscribeAsync("Trigger", triggerId.ToString());
    }

    public Task LeaveTrigger(Guid triggerId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"trigger:{triggerId}");

    /// <summary>Records who subscribed to which scope (best-effort; never fails the subscription).</summary>
    private async Task AuditSubscribeAsync(string entityType, string entityId)
    {
        try
        {
            await audit.AddAsync(new AuditEntry
            {
                Id = Guid.NewGuid(),
                Action = "subscribe",
                EntityType = entityType,
                EntityId = entityId,
                Actor = Context.User.Actor(),
                Source = nameof(JobSource.Manager),
                Detail = $"connection={Context.ConnectionId}",
            });
        }
        catch { /* subscription audit is best-effort */ }
    }
}
