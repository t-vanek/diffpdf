using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>
/// Fans a <see cref="JobProgressChanged"/> out to the job, instance and branch groups, plus the global "scope"
/// group so the Jobs list updates live for every job regardless of launch source (trigger / manager / API).
/// The per-branch sequential queue keeps concurrent jobs few, so the scope broadcast stays light.
/// </summary>
public sealed class SignalRJobProgressPublisher(IHubContext<JobsHub> hub) : IJobProgressPublisher
{
    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default)
    {
        return Task.WhenAll(
            hub.Clients.Group($"job:{progress.JobId}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"instance:{progress.BranchKey}:{progress.InstanceKey}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"branch:{progress.BranchKey}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group("scope").SendAsync("jobProgress", progress, ct));
    }
}
