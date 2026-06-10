using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Phase-level engine telemetry, published through the <c>DiffPdf.Engine</c> meter (scraped at <c>/metrics</c>
/// and/or pushed over OTLP). Complements <c>DiffPdf.Compare</c> (whole-pair wall-clock) and <c>DiffPdf.Render</c>
/// (per-page renders) by splitting the remaining engine time into its phases, so a slow pair can be attributed
/// to extraction, pixel comparison, blank detection or highlight writing without attaching a profiler.
/// A static meter keeps the engine free of constructor wiring; no-op overhead when nothing listens.
/// </summary>
internal static class EngineMetrics
{
    public const string MeterName = "DiffPdf.Engine";

    /// <summary>Covers the combined probe + text extraction (one document open per side).</summary>
    public const string Extract = "extract";
    public const string TextDiff = "text.diff";
    public const string PixelCompare = "pixel.compare";
    public const string BlankDetect = "blank.detect";
    public const string HighlightWrite = "highlight.write";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> PhaseDuration = Meter.CreateHistogram<double>(
        "diffpdf.engine.phase.duration", unit: "s",
        description: "Wall-clock of one comparison-engine phase, tagged by phase.");

    /// <summary>Records the time elapsed since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    public static void Record(string phase, long startTimestamp) =>
        PhaseDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
            new KeyValuePair<string, object?>("phase", phase));
}
