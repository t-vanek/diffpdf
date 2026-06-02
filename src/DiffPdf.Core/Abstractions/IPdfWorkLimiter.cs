using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Abstractions;

/// <summary>
/// Caps the number of concurrent CPU/RAM-heavy PDF operations (rendering) across
/// the whole process, regardless of how many jobs or file pairs run in parallel.
/// </summary>
public interface IPdfWorkLimiter
{
    ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default);
}

public sealed class PdfWorkLimiterOptions
{
    /// <summary>Maximum concurrent PDF render operations process-wide.</summary>
    public int MaxConcurrentOperations { get; set; } = 4;
}

/// <summary>Semaphore-backed global limiter.</summary>
public sealed class SemaphorePdfWorkLimiter : IPdfWorkLimiter, IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public SemaphorePdfWorkLimiter(IOptions<PdfWorkLimiterOptions> options)
    {
        int max = Math.Max(1, options.Value.MaxConcurrentOperations);
        _semaphore = new SemaphoreSlim(max, max);
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
