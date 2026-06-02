using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Network;
using DiffPdf.Pdf.Network;
using DiffPdf.Pdf.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Pdf.DependencyInjection;

public static class DiffPdfServiceCollectionExtensions
{
    /// <summary>Registers the full comparison stack (engine, renderers, writers).</summary>
    public static IServiceCollection AddDiffPdf(this IServiceCollection services)
    {
        services.AddOptions<GhostscriptOptions>();
        services.AddOptions<PdfWorkLimiterOptions>();
        services.AddOptions<NetworkOptions>();

        // Global cap on concurrent render operations (CPU/RAM protection)
        services.AddSingleton<IPdfWorkLimiter, SemaphorePdfWorkLimiter>();

        // PDF primitives
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IImageComparer, SkiaImageComparer>();
        services.AddSingleton<IBlankPageDetector, SkiaBlankPageDetector>();
        services.AddSingleton<IHighlightedPdfWriter, RasterHighlightPdfWriter>();
        services.AddSingleton<IHighlightedPdfWriter, VectorHighlightPdfWriter>();
        services.AddSingleton<IHighlightedPdfWriterFactory, HighlightedPdfWriterFactory>();

        // Renderers + factory
        services.AddSingleton<IPdfPageRenderer, GhostscriptPageRenderer>();
        services.AddSingleton<IPdfPageRenderer, PdfiumPageRenderer>();
        services.AddSingleton<IPdfPageRendererFactory, RendererFactory>();

        // Comparison logic
        services.AddSingleton<ITextComparer, TextComparer>();
        services.AddSingleton<IPageAligner, PageAligner>();
        services.AddSingleton<IContentErrorDetector, ContentErrorDetector>();
        services.AddSingleton<IComparisonEngine, ComparisonEngine>();
        services.AddSingleton<IBatchComparer, BatchComparer>();

        // Network share access (no-op for local / already-mounted paths) + the
        // config-driven resolver and the discovery service.
        services.AddSingleton<INetworkShareConnector, PlatformShareConnector>();
        services.AddSingleton<INetworkShareResolver, NetworkShareResolver>();
        services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();

        return services;
    }
}
