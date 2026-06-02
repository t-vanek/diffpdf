using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace DiffPdf.Pdf;

/// <summary>
/// "Variant A" highlighted diff PDF: the original PDF pages are placed as vector
/// form XObjects (so their text stays selectable) and the difference regions are
/// drawn as colored overlays on top. Supports the same side-by-side / single
/// layouts as the raster writer.
/// </summary>
public sealed class VectorHighlightPdfWriter : IHighlightedPdfWriter
{
    public HighlightStyle Style => HighlightStyle.VectorOverlay;

    private const double HeaderHeight = 18;
    private const double Gap = 16;

    private static readonly XColor OldHeader = XColor.FromArgb(120, 220, 60, 60);
    private static readonly XColor NewHeader = XColor.FromArgb(120, 60, 200, 90);
    private static readonly XColor AbsentHeader = XColor.FromArgb(120, 170, 170, 170);
    private static readonly XColor AbsentFill = XColor.FromArgb(40, 170, 170, 170);

    private static readonly XFont? HeaderFont = TryCreateFont();

    private static XFont? TryCreateFont()
    {
        if (SansFontResolver.Instance is null) return null;
        GlobalFontSettings.FontResolver ??= SansFontResolver.Instance;
        try { return new XFont(SansFontResolver.FaceName, 9, XFontStyleEx.Bold); }
        catch { return null; }
    }

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
        var side = spread.New ?? spread.Old;
        if (side is null) return;
        bool isNew = spread.New is not null;

        using var form = OpenForm(side);
        double wPt = form.PointWidth, hPt = form.PointHeight;

        var page = document.AddPage();
        page.Width = XUnit.FromPoint(wPt);
        page.Height = XUnit.FromPoint(hPt + HeaderHeight);

        using var gfx = XGraphics.FromPdfPage(page);
        DrawHeader(gfx, 0, wPt, isNew ? NewHeader : OldHeader,
            isNew ? NewLabel(spread.NewPageNumber) : OldLabel(spread.OldPageNumber));
        DrawSide(gfx, form, side, xOffset: 0, yOffset: HeaderHeight, wPt, hPt);
    }

    private static void DrawSideBySide(PdfDocument document, DiffSpread spread)
    {
        using var oldForm = spread.Old is not null ? OpenForm(spread.Old) : null;
        using var newForm = spread.New is not null ? OpenForm(spread.New) : null;

        var reference = oldForm ?? newForm;
        if (reference is null) return;

        double oldW = oldForm?.PointWidth ?? reference.PointWidth;
        double oldH = oldForm?.PointHeight ?? reference.PointHeight;
        double newW = newForm?.PointWidth ?? reference.PointWidth;
        double newH = newForm?.PointHeight ?? reference.PointHeight;

        var page = document.AddPage();
        page.Width = XUnit.FromPoint(oldW + Gap + newW);
        page.Height = XUnit.FromPoint(Math.Max(oldH, newH) + HeaderHeight);
        using var gfx = XGraphics.FromPdfPage(page);

        DrawHeader(gfx, 0, oldW, spread.Old is not null ? OldHeader : AbsentHeader, OldLabel(spread.OldPageNumber));
        if (oldForm is not null && spread.Old is not null)
            DrawSide(gfx, oldForm, spread.Old, 0, HeaderHeight, oldW, oldH);
        else
            gfx.DrawRectangle(new XSolidBrush(AbsentFill), new XRect(0, HeaderHeight, oldW, oldH));

        double rightX = oldW + Gap;
        DrawHeader(gfx, rightX, newW, spread.New is not null ? NewHeader : AbsentHeader, NewLabel(spread.NewPageNumber));
        if (newForm is not null && spread.New is not null)
            DrawSide(gfx, newForm, spread.New, rightX, HeaderHeight, newW, newH);
        else
            gfx.DrawRectangle(new XSolidBrush(AbsentFill), new XRect(rightX, HeaderHeight, newW, newH));
    }

    private static XPdfForm OpenForm(HighlightSide side)
    {
        var form = XPdfForm.FromFile(side.SourcePdfPath);
        form.PageNumber = side.PageNumber; // 1-based
        return form;
    }

    private static void DrawSide(XGraphics gfx, XPdfForm form, HighlightSide side, double xOffset, double yOffset, double wPt, double hPt)
    {
        gfx.DrawImage(form, xOffset, yOffset, wPt, hPt); // vector form — text stays selectable

        foreach (var region in side.Regions)
        {
            var box = region.BoundingBox;
            double x = xOffset + box.X;
            double y = yOffset + (hPt - (box.Y + box.Height)); // points bottom-left -> top-left
            var (border, fill) = ColorsFor(region.Kind);
            gfx.DrawRectangle(new XPen(border, 1.2), new XSolidBrush(fill), new XRect(x, y, box.Width, box.Height));
        }
    }

    private static string OldLabel(int? page) => page is int p ? $"OLD  p.{p}" : "(no old page)";
    private static string NewLabel(int? page) => page is int p ? $"NEW  p.{p}" : "(no new page)";

    private static void DrawHeader(XGraphics gfx, double x, double width, XColor color, string label)
    {
        gfx.DrawRectangle(new XSolidBrush(color), new XRect(x, 0, width, HeaderHeight));
        if (HeaderFont is not null)
        {
            try
            {
                gfx.DrawString(label, HeaderFont, XBrushes.Black,
                    new XRect(x + 6, 0, width - 12, HeaderHeight), XStringFormats.CenterLeft);
            }
            catch { /* best-effort */ }
        }
    }

    private static (XColor Border, XColor Fill) ColorsFor(DifferenceKind kind) => kind switch
    {
        DifferenceKind.Added => (XColor.FromArgb(255, 0, 160, 0), XColor.FromArgb(70, 0, 200, 0)),
        DifferenceKind.Removed => (XColor.FromArgb(255, 200, 0, 0), XColor.FromArgb(70, 255, 0, 0)),
        _ => (XColor.FromArgb(255, 220, 140, 0), XColor.FromArgb(70, 255, 170, 0)),
    };
}
