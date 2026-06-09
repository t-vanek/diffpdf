using System.Diagnostics;

namespace DiffPdf.Messaging.Observability;

/// <summary>
/// Application <see cref="ActivitySource"/> for domain spans on the comparison pipeline. Registered with
/// OpenTelemetry via <c>AddSource("DiffPdf.Comparison")</c>; spans nest under Wolverine's auto-propagated
/// message activities, so a whole job reads as one trace (HTTP → messages → per-pair comparison) when an OTLP
/// exporter is configured. No-op (zero overhead) when no tracing listener is attached.
/// </summary>
public static class DiffPdfTracing
{
    public const string SourceName = "DiffPdf.Comparison";

    public static readonly ActivitySource Source = new(SourceName);
}
