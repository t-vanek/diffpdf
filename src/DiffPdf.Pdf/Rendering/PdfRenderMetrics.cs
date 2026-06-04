using System.Diagnostics.Metrics;
using DiffPdf.Core.Models;

namespace DiffPdf.Pdf.Rendering;

/// <summary>
/// Page-render telemetry, published through the <c>DiffPdf.Render</c> meter (scraped at <c>/metrics</c> and/or
/// pushed over OTLP). A static meter keeps the renderers free of constructor wiring; the only instrument is a
/// duration histogram tagged by backend and outcome (<c>ok</c> | <c>timeout</c> | <c>error</c>).
/// </summary>
internal static class PdfRenderMetrics
{
    public const string MeterName = "DiffPdf.Render";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "diffpdf.render.duration", unit: "s", description: "Duration of a single PDF page render.");

    public static void Record(RendererBackend backend, double seconds, string outcome) =>
        Duration.Record(seconds,
            new KeyValuePair<string, object?>("backend", backend.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome));
}
