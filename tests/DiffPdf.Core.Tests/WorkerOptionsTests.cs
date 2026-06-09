using DiffPdf.Worker;
using DiffPdf.Worker.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Invariants on <see cref="WorkerOptions"/>: the per-pair lease must outlive the per-pair comparison cap (or
/// the stale-lease sweeper reclaims a still-running pair and runs it twice), and the worker registration must
/// reject a nonsensical config at startup rather than limp along with it.
/// </summary>
public class WorkerOptionsTests
{
    [Fact]
    public void FilePairLease_exceeds_comparison_timeout_at_defaults()
    {
        var o = new WorkerOptions();
        Assert.True(o.FilePairLease > o.FilePairComparisonTimeout);
        Assert.Equal(TimeSpan.FromMinutes(12), o.FilePairLease); // 10 min cap + 2 min slack
    }

    [Fact]
    public void FilePairLease_tracks_comparison_timeout_when_raised()
    {
        var o = new WorkerOptions { FilePairComparisonTimeoutMinutes = 20 };
        Assert.True(o.FilePairLease > o.FilePairComparisonTimeout);
        Assert.Equal(TimeSpan.FromMinutes(22), o.FilePairLease);
    }

    [Fact]
    public void Validation_accepts_the_default_config() =>
        Assert.Null(Record.Exception(() => Resolve(_ => { })));

    [Fact]
    public void Validation_rejects_nonpositive_JobLockMinutes() =>
        AssertInvalid(o => o.JobLockMinutes = 0);

    [Fact]
    public void Validation_rejects_nonpositive_FilePairComparisonTimeout() =>
        AssertInvalid(o => o.FilePairComparisonTimeoutMinutes = 0);

    [Fact]
    public void Validation_rejects_zero_MaxFilePairAttempts() =>
        AssertInvalid(o => o.MaxFilePairAttempts = 0);

    [Fact]
    public void Validation_rejects_nonpositive_StaleRecoveryInterval() =>
        AssertInvalid(o => o.StaleRecoveryIntervalSeconds = 0);

    private static void AssertInvalid(Action<WorkerOptions> mutate) =>
        Assert.Throws<OptionsValidationException>(() => Resolve(mutate));

    /// <summary>Builds the worker option pipeline and resolves the value, forcing the registered validators to run.</summary>
    private static WorkerOptions Resolve(Action<WorkerOptions> mutate)
    {
        var services = new ServiceCollection();
        services.AddDiffPdfWorker();
        services.Configure(mutate);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<WorkerOptions>>().Value;
    }
}
