using System.Threading.Channels;

namespace DiffPdf.Worker;

/// <summary>Hands queued job ids to the background processor.</summary>
public interface IComparisonJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}

/// <summary>Unbounded in-memory channel queue for the single-instance MVP.</summary>
public sealed class ChannelComparisonJobQueue : IComparisonJobQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
