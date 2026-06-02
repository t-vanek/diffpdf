using DiffPdf.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class PdfWorkLimiterTests
{
    private static SemaphorePdfWorkLimiter Limiter(int max) =>
        new(Options.Create(new PdfWorkLimiterOptions { MaxConcurrentOperations = max }));

    [Fact]
    public async Task BlocksBeyondLimit_UntilReleased()
    {
        var limiter = Limiter(1);

        var first = await limiter.AcquireAsync();
        var secondTask = limiter.AcquireAsync().AsTask();

        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted); // capacity exhausted

        first.Dispose();
        var second = await secondTask;        // now granted
        Assert.True(secondTask.IsCompletedSuccessfully);
        second.Dispose();
    }

    [Fact]
    public async Task AllowsConcurrencyUpToLimit()
    {
        var limiter = Limiter(2);
        var a = await limiter.AcquireAsync();
        var b = await limiter.AcquireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        a.Dispose();
        b.Dispose();
    }
}
