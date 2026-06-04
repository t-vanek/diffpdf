using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace DiffPdf.Messaging.Observability;

/// <summary>
/// Application metrics for the per-branch sequential queue, published through the <c>DiffPdf.Queue</c> meter
/// (scraped at <c>/metrics</c> and/or pushed over OTLP). Registered as a singleton.
///
/// <para>Queue depth is an <b>observable gauge</b> backed by an in-memory snapshot the dispatcher refreshes on
/// every state publish (and at least once per dispatcher tick), so a scrape never blocks on the database.
/// Job duration and outcome are recorded at the terminal transition (complete / fail).</para>
/// </summary>
public sealed class DiffPdfMetrics : IDisposable
{
    public const string MeterName = "DiffPdf.Queue";

    private readonly Meter _meter;
    private readonly Histogram<double> _jobDuration;
    private readonly Counter<long> _jobsFinished;
    private readonly ConcurrentDictionary<Guid, BranchDepth> _depths = new();

    public DiffPdfMetrics()
    {
        _meter = new Meter(MeterName);
        _jobDuration = _meter.CreateHistogram<double>(
            "diffpdf.job.duration", unit: "s",
            description: "Wall-clock duration of a comparison job that reached a terminal state.");
        _jobsFinished = _meter.CreateCounter<long>(
            "diffpdf.jobs.finished", unit: "{job}",
            description: "Comparison jobs that reached a terminal state, tagged by result.");
        _meter.CreateObservableGauge(
            "diffpdf.queue.depth", ObserveDepth, unit: "{job}",
            description: "Active comparison jobs per branch, by queue state.");
    }

    /// <summary>Refreshes the queue-depth snapshot for a branch (called by the dispatcher on every state publish).</summary>
    public void RecordBranchDepth(Guid branchId, string branchKey, int pending, int queued, int running, int paused) =>
        _depths[branchId] = new BranchDepth(branchKey, pending, queued, running, paused);

    /// <summary>Drops a deleted branch from the snapshot so its gauge stops reporting.</summary>
    public void RemoveBranch(Guid branchId) => _depths.TryRemove(branchId, out _);

    /// <summary>Records a job that reached a terminal state. <paramref name="result"/> is <c>passed</c> | <c>gate_violated</c> | <c>failed</c>.</summary>
    public void RecordJobFinished(string result, TimeSpan duration)
    {
        _jobsFinished.Add(1, new KeyValuePair<string, object?>("result", result));
        if (duration > TimeSpan.Zero)
            _jobDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("result", result));
    }

    private IEnumerable<Measurement<int>> ObserveDepth()
    {
        foreach (var (_, d) in _depths)
        {
            yield return Depth(d.Pending, d.BranchKey, "pending");
            yield return Depth(d.Queued, d.BranchKey, "queued");
            yield return Depth(d.Running, d.BranchKey, "running");
            yield return Depth(d.Paused, d.BranchKey, "paused");
        }
    }

    private static Measurement<int> Depth(int value, string branch, string state) =>
        new(value, new KeyValuePair<string, object?>("branch", branch), new KeyValuePair<string, object?>("state", state));

    public void Dispose() => _meter.Dispose();

    private readonly record struct BranchDepth(string BranchKey, int Pending, int Queued, int Running, int Paused);
}
