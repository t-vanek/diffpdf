using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging;

/// <summary>
/// Bridges <see cref="IProgress{T}"/> (called concurrently from the batch's
/// parallel loop) to sequential, version-checked persistence updates. Updates
/// are chained so DB writes never race; <see cref="DrainAsync"/> awaits all
/// pending writes before the caller reads <see cref="Current"/>.
/// </summary>
public sealed class JobProgressTracker(
    IJobStore jobStore,
    IJobProgressPublisher publisher,
    ComparisonJob job,
    ILogger logger) : IProgress<BatchProgress>
{
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    private volatile ComparisonJob _current = job;
    private int _lastProcessed = -1;

    public ComparisonJob Current => _current;

    public void Report(BatchProgress value)
    {
        lock (_gate)
        {
            _pending = _pending.ContinueWith(_ => ApplyAsync(value), TaskScheduler.Default).Unwrap();
        }
    }

    public Task DrainAsync()
    {
        lock (_gate) { return _pending; }
    }

    private async Task ApplyAsync(BatchProgress value)
    {
        if (value.Processed <= _lastProcessed) return;
        _lastProcessed = value.Processed;
        try
        {
            _current = await jobStore.UpdateProgressAsync(
                _current.Id, value.Processed, value.Total, _current.Version, CancellationToken.None);
            await publisher.PublishAsync(JobProgressChanged.From(_current), CancellationToken.None);
        }
        catch (ConcurrencyConflictException ex)
        {
            logger.LogWarning(ex, "Progress update conflict for job {JobId}", _current.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Progress update failed for job {JobId}", _current.Id);
        }
    }
}
