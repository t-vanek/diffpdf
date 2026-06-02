using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>Fans a <see cref="JobProgressChanged"/> out to the job, instance and branch groups.</summary>
public sealed class SignalRJobProgressPublisher(IHubContext<JobsHub> hub) : IJobProgressPublisher
{
    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default)
    {
        return Task.WhenAll(
            hub.Clients.Group($"job:{progress.JobId}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"instance:{progress.BranchKey}:{progress.InstanceKey}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"branch:{progress.BranchKey}").SendAsync("jobProgress", progress, ct));
    }
}
