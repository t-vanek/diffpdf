using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Realtime job-progress notifications. Clients join a job, instance or branch
/// group and receive "jobProgress" events. This is a notification channel only —
/// REST remains the source of truth, so a client that misses an event can reload
/// state via <c>GET /api/jobs/{id}</c>.
/// </summary>
public sealed class JobsHub : Hub
{
    public Task JoinJob(Guid jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task LeaveJob(Guid jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task JoinInstance(string branchKey, string instanceKey) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"instance:{branchKey}:{instanceKey}");

    public Task JoinBranch(string branchKey) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"branch:{branchKey}");

    public Task JoinTrigger(Guid triggerId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"trigger:{triggerId}");

    public Task LeaveTrigger(Guid triggerId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"trigger:{triggerId}");

    /// <summary>
    /// Join the global scope group. Branch/instance CRUD events (<c>branch.*</c> / <c>instance.*</c>) are
    /// fanned here in addition to the per-branch group, so a client can hear about scope changes it isn't
    /// otherwise subscribed to — e.g. a branch created elsewhere it has never joined.
    /// </summary>
    public Task JoinScope() =>
        Groups.AddToGroupAsync(Context.ConnectionId, "scope");

    public Task LeaveScope() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, "scope");
}
