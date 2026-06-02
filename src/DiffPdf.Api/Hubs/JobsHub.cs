using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Realtime job-progress notifications. Clients join a job, project or
/// business-instance group and receive "jobProgress" events. This is a
/// notification channel only — REST remains the source of truth, so a client
/// that misses an event can reload state via <c>GET /api/jobs/{id}</c>.
/// </summary>
public sealed class JobsHub : Hub
{
    public Task JoinJob(Guid jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task LeaveJob(Guid jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task JoinProject(string businessInstanceKey, string projectKey) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"project:{businessInstanceKey}:{projectKey}");

    public Task JoinBusinessInstance(string businessInstanceKey) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"business-instance:{businessInstanceKey}");
}
