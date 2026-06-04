using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>Pushes a <see cref="BranchQueueState"/> to the branch group so the Branches/Instances views update live.</summary>
public sealed class SignalRBranchQueueStatePublisher(IHubContext<JobsHub> hub) : IBranchQueueStatePublisher
{
    public Task PublishAsync(BranchQueueState state, CancellationToken ct = default) =>
        hub.Clients.Group($"branch:{state.BranchKey}").SendAsync("queueState", state, ct);
}
