using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using PDFtoImage;
using SkiaSharp;

namespace DiffPdf.Pdf.Rendering;

/// <summary>
/// Renders pages via PDFium (BSD) using the PDFtoImage wrapper. Use as a
/// license-clean alternative/fallback to Ghostscript.
/// </summary>
public sealed class PdfiumPageRenderer : IPdfPageRenderer
{
    public RendererBackend Backend => RendererBackend.Pdfium;

    public int GetPageCount(string path)
    {
        byte[] pdf = File.ReadAllBytes(path);
        return Conversion.GetPageCount(pdf);
    }

    public async Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default)
    {
        byte[] pdf = await File.ReadAllBytesAsync(path, ct);

        // PDFtoImage page index is 0-based.
        using SKBitmap bitmap = Conversion.ToImage(
            pdf,
            password: null,
            page: pageNumber - 1,
            options: new RenderOptions(Dpi: dpi));

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        return new RenderedPage
        {
            PageNumber = pageNumber,
            Dpi = dpi,
            WidthPx = bitmap.Width,
            HeightPx = bitmap.Height,
            Png = data.ToArray(),
        };
    }
}
