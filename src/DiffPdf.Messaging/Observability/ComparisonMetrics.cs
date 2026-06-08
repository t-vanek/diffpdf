using System.Diagnostics.Metrics;

namespace DiffPdf.Messaging.Observability;

/// <summary>
/// Per-file-pair comparison telemetry, published through the <c>DiffPdf.Compare</c> meter (scraped at
/// <c>/metrics</c> and/or pushed over OTLP). A static meter keeps the handler free of constructor wiring. The
/// single instrument is a duration histogram tagged by outcome (<c>ok</c> | <c>timeout</c> | <c>error</c>)
/// measuring the comparison <b>engine alone</b> (probe + extract + render + highlight) — i.e. it excludes the
/// queue wait, the claim/complete DB writes, and the progress/finalize bookkeeping around it.
/// </summary>
internal static class ComparisonMetrics
{
    public const string MeterName = "DiffPdf.Compare";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "diffpdf.pair.compare.duration", unit: "s",
        description: "Wall-clock of comparing a single file pair (engine only), by outcome.");

    public static void Record(TimeSpan elapsed, string outcome) =>
        Duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
}
