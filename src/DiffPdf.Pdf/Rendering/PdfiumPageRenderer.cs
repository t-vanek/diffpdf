using System.Diagnostics;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

// PDFtoImage's PDFium calls are annotated as platform-specific (CA1416). They are supported on every platform
// DiffPdf runs on (Windows service, Linux, macOS), so the analyzer's guard requirement does not apply here.
#pragma warning disable CA1416

namespace DiffPdf.Pdf.Rendering;

/// <summary>
/// Renders pages via PDFium (BSD) using the PDFtoImage wrapper. Use as a
/// license-clean alternative/fallback to Ghostscript.
/// </summary>
public sealed class PdfiumPageRenderer(IPdfWorkLimiter limiter, IOptions<PdfiumOptions> options) : IPdfPageRenderer
{
    private readonly PdfiumOptions _options = options.Value;

    public RendererBackend Backend => RendererBackend.Pdfium;

    public int GetPageCount(string path)
    {
        byte[] pdf = File.ReadAllBytes(path);
        return Conversion.GetPageCount(pdf);
    }

    public async Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default)
    {
        using var _ = await limiter.AcquireAsync(ct);
        byte[] pdf = await File.ReadAllBytesAsync(path, ct);

        // Conversion.ToImage is a blocking native call that ignores cancellation; run it off the await path and
        // cap it so a corrupt/oversized PDF can't wedge a worker thread forever. On timeout the await returns and
        // the abandoned task ends once the native call completes (PDFium cannot be force-killed in-process).
        var sw = Stopwatch.StartNew();
        string outcome = "error";
        try
        {
            var render = Task.Run(() =>
            {
                // PDFtoImage page index is 0-based.
                using SKBitmap bitmap = Conversion.ToImage(
                        pdf, password: null, page: pageNumber - 1, options: new RenderOptions(Dpi: dpi))
                    ?? throw new InvalidOperationException($"PDFium produced no image for page {pageNumber}.");
                using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

                return new RenderedPage
                {
                    PageNumber = pageNumber,
                    Dpi = dpi,
                    WidthPx = bitmap.Width,
                    HeightPx = bitmap.Height,
                    Png = data.ToArray(),
                };
            }, ct);

            var result = await render.WaitAsync(_options.Timeout, ct);
            outcome = "ok";
            return result;
        }
        catch (TimeoutException)
        {
            outcome = "timeout";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PdfRenderMetrics.Record(Backend, sw.Elapsed.TotalSeconds, outcome);
        }
    }
}
