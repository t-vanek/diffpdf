using DiffPdf.Messaging.Scheduling;

namespace DiffPdf.Core.Tests;

/// <summary>Test double for <see cref="IBatchLauncher"/>: records the calls and returns a configurable outcome (no real job).</summary>
public sealed class StubBatchLauncher : IBatchLauncher
{
    public List<(string Branch, string Instance)> Calls { get; } = [];

    /// <summary>The result every call returns. Defaults to "nothing to compare" (no job launched).</summary>
    public LaunchResult Result { get; set; } = new(LaunchOutcome.NothingToCompare);

    public Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, CancellationToken ct = default)
    {
        Calls.Add((branchKey, instanceKey));
        return Task.FromResult(Result);
    }
}
