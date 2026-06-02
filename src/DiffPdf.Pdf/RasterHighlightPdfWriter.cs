using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DiffPdf.Pdf;

/// <summary>
/// "Variant B" highlighted PDF: each differing page is rendered to a raster
/// background and the difference regions are drawn as colored overlays.
/// Uniform across text and visual modes; output is image-based.
/// </summary>
public sealed class RasterHighlightPdfWriter : IHighlightedPdfWriter
{
    public Task WriteAsync(string outputPath, IReadOnlyList<HighlightedPage> pages, CancellationToken ct = default)
    {
        using var document = new PdfDocument();
        document.Info.Title = "diffpdf comparison";

        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();

            int dpi = page.Background.Dpi <= 0 ? 150 : page.Background.Dpi;
            double widthPt = page.Background.WidthPx * 72.0 / dpi;
            double heightPt = page.Background.HeightPx * 72.0 / dpi;

            var pdfPage = document.AddPage();
            pdfPage.Width = XUnit.FromPoint(widthPt);
            pdfPage.Height = XUnit.FromPoint(heightPt);

            using var gfx = XGraphics.FromPdfPage(pdfPage);
            using (var imageStream = new MemoryStream(page.Background.Png))
            using (var image = XImage.FromStream(imageStream))
            {
                gfx.DrawImage(image, 0, 0, widthPt, heightPt);
            }

            foreach (var region in page.Regions)
            {
                // Regions are stored in PDF points (bottom-left); XGraphics is top-left.
                double yTop = heightPt - (region.BoundingBox.Y + region.BoundingBox.Height);
                var rect = new XRect(region.BoundingBox.X, yTop, region.BoundingBox.Width, region.BoundingBox.Height);

                var (border, fill) = ColorsFor(region.Kind);
                gfx.DrawRectangle(new XPen(border, 1.2), new XSolidBrush(fill), rect);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        document.Save(outputPath);
        return Task.CompletedTask;
    }

    private static (XColor Border, XColor Fill) ColorsFor(DifferenceKind kind) => kind switch
    {
        DifferenceKind.Added => (XColor.FromArgb(255, 0, 160, 0), XColor.FromArgb(70, 0, 200, 0)),
        DifferenceKind.Removed => (XColor.FromArgb(255, 200, 0, 0), XColor.FromArgb(70, 255, 0, 0)),
        _ => (XColor.FromArgb(255, 220, 140, 0), XColor.FromArgb(70, 255, 170, 0)),
    };
}
