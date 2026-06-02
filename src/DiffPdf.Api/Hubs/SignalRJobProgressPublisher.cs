using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace DiffPdf.Api.Hubs;

/// <summary>Fans a <see cref="JobProgressChanged"/> out to the job, project and business-instance groups.</summary>
public sealed class SignalRJobProgressPublisher(IHubContext<JobsHub> hub) : IJobProgressPublisher
{
    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default)
    {
        return Task.WhenAll(
            hub.Clients.Group($"job:{progress.JobId}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"project:{progress.BusinessInstanceKey}:{progress.ProjectKey}").SendAsync("jobProgress", progress, ct),
            hub.Clients.Group($"business-instance:{progress.BusinessInstanceKey}").SendAsync("jobProgress", progress, ct));
    }
}
