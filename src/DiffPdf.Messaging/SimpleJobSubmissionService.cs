using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Wolverine;

namespace DiffPdf.Messaging;

/// <summary>Dev/in-memory submission: create the job, then publish the command (not transactional).</summary>
public sealed class SimpleJobSubmissionService(IJobStore jobStore, IMessageBus bus) : IJobSubmissionService
{
    public async Task SubmitAsync(ComparisonJob job, object command, CancellationToken ct = default)
    {
        await jobStore.CreateAsync(job, ct);
        await bus.PublishAsync(command);
    }
}
