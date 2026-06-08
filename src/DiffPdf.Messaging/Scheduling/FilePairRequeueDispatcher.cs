using DiffPdf.Application.Abstractions;
using DiffPdf.Messaging.Messages;
using Wolverine;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Wolverine-backed implementation of <see cref="IFilePairRequeueDispatcher"/>: publishes a
/// <see cref="CompareFilePair"/> so a worker re-runs the pair. Keeps the messaging transport out of the
/// application layer (the job retry path depends only on the abstraction).
/// </summary>
public sealed class FilePairRequeueDispatcher(IMessageBus bus) : IFilePairRequeueDispatcher
{
    public Task RequeueAsync(Guid jobId, Guid taskId, CancellationToken ct = default) =>
        bus.PublishAsync(new CompareFilePair(jobId, taskId)).AsTask();
}
