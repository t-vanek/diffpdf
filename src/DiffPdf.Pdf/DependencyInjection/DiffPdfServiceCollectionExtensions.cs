using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Pdf.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Pdf.DependencyInjection;

public static class DiffPdfServiceCollectionExtensions
{
    /// <summary>Registers the full comparison stack (engine, renderers, writers).</summary>
    public static IServiceCollection AddDiffPdf(this IServiceCollection services)
    {
        services.AddOptions<GhostscriptOptions>();

        // PDF primitives
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IImageComparer, SkiaImageComparer>();
        services.AddSingleton<IBlankPageDetector, SkiaBlankPageDetector>();
        services.AddSingleton<IHighlightedPdfWriter, RasterHighlightPdfWriter>();

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

        return services;
    }
}
