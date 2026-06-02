using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DiffPdf.Pdf;

/// <summary>
/// "Variant B" highlighted diff PDF. In <see cref="HighlightLayout.SideBySide"/>
/// each differing page is a spread with the old render on the left (removed
/// content in red) and the new render on the right (added in green, visual
/// changes in orange). A colored header strip marks each side (red = old,
/// green = new, gray = absent). Font-free so it needs no font resolver.
/// </summary>
public sealed class RasterHighlightPdfWriter : IHighlightedPdfWriter
{
    private const double HeaderHeight = 16;
    private const double Gap = 16;

    private static readonly XColor OldHeader = XColor.FromArgb(120, 220, 60, 60);
    private static readonly XColor NewHeader = XColor.FromArgb(120, 60, 200, 90);
    private static readonly XColor AbsentHeader = XColor.FromArgb(120, 170, 170, 170);
    private static readonly XColor AbsentFill = XColor.FromArgb(40, 170, 170, 170);

    public Task WriteAsync(
        string outputPath, IReadOnlyList<DiffSpread> spreads, HighlightLayout layout, CancellationToken ct = default)
    {
        using var document = new PdfDocument();
        document.Info.Title = "diffpdf comparison";

        foreach (var spread in spreads)
        {
            ct.ThrowIfCancellationRequested();
            if (layout == HighlightLayout.Single)
                DrawSingle(document, spread);
            else
                DrawSideBySide(document, spread);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        document.Save(outputPath);
        return Task.CompletedTask;
    }

    private static void DrawSingle(PdfDocument document, DiffSpread spread)
    {
        // Prefer the new side; fall back to old (removed pages).
        var side = spread.New ?? spread.Old;
        if (side is null) return;
        bool isNew = spread.New is not null;

        var (wPt, hPt) = SizePt(side.Render);
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(wPt);
        page.Height = XUnit.FromPoint(hPt + HeaderHeight);

        using var gfx = XGraphics.FromPdfPage(page);
        DrawHeader(gfx, 0, wPt, isNew ? NewHeader : OldHeader);
        DrawSide(gfx, side, xOffset: 0, yOffset: HeaderHeight, wPt, hPt);
    }

    private static void DrawSideBySide(PdfDocument document, DiffSpread spread)
    {
        // Use whichever render exists to size an absent column.
        var reference = (spread.Old ?? spread.New)!.Render;
        var (oldW, oldH) = spread.Old is not null ? SizePt(spread.Old.Render) : SizePt(reference);
        var (newW, newH) = spread.New is not null ? SizePt(spread.New.Render) : SizePt(reference);

        double pageW = oldW + Gap + newW;
        double pageH = Math.Max(oldH, newH) + HeaderHeight;

        var page = document.AddPage();
        page.Width = XUnit.FromPoint(pageW);
        page.Height = XUnit.FromPoint(pageH);
        using var gfx = XGraphics.FromPdfPage(page);

        // Left = old.
        DrawHeader(gfx, 0, oldW, spread.Old is not null ? OldHeader : AbsentHeader);
        if (spread.Old is not null)
            DrawSide(gfx, spread.Old, xOffset: 0, yOffset: HeaderHeight, oldW, oldH);
        else
            DrawAbsent(gfx, 0, HeaderHeight, oldW, oldH);

        // Right = new.
        double rightX = oldW + Gap;
        DrawHeader(gfx, rightX, newW, spread.New is not null ? NewHeader : AbsentHeader);
        if (spread.New is not null)
            DrawSide(gfx, spread.New, xOffset: rightX, yOffset: HeaderHeight, newW, newH);
        else
            DrawAbsent(gfx, rightX, HeaderHeight, newW, newH);
    }

    private static void DrawHeader(XGraphics gfx, double x, double width, XColor color) =>
        gfx.DrawRectangle(new XSolidBrush(color), new XRect(x, 0, width, HeaderHeight));

    private static void DrawAbsent(XGraphics gfx, double x, double y, double width, double height) =>
        gfx.DrawRectangle(new XSolidBrush(AbsentFill), new XRect(x, y, width, height));

    private static void DrawSide(XGraphics gfx, HighlightSide side, double xOffset, double yOffset, double wPt, double hPt)
    {
        using (var stream = new MemoryStream(side.Render.Png))
        using (var image = XImage.FromStream(stream))
        {
            gfx.DrawImage(image, xOffset, yOffset, wPt, hPt);
        }

        foreach (var region in side.Regions)
        {
            var box = region.BoundingBox;
            // Region is in PDF points (bottom-left of its own page) -> spread top-left.
            double x = xOffset + box.X;
            double y = yOffset + (hPt - (box.Y + box.Height));
            var rect = new XRect(x, y, box.Width, box.Height);

            var (border, fill) = ColorsFor(region.Kind);
            gfx.DrawRectangle(new XPen(border, 1.2), new XSolidBrush(fill), rect);
        }
    }

    private static (double W, double H) SizePt(RenderedPage page)
    {
        int dpi = page.Dpi <= 0 ? 150 : page.Dpi;
        return (page.WidthPx * 72.0 / dpi, page.HeightPx * 72.0 / dpi);
    }

    private static (XColor Border, XColor Fill) ColorsFor(DifferenceKind kind) => kind switch
    {
        DifferenceKind.Added => (XColor.FromArgb(255, 0, 160, 0), XColor.FromArgb(70, 0, 200, 0)),
        DifferenceKind.Removed => (XColor.FromArgb(255, 200, 0, 0), XColor.FromArgb(70, 255, 0, 0)),
        _ => (XColor.FromArgb(255, 220, 140, 0), XColor.FromArgb(70, 255, 170, 0)),
    };
}
