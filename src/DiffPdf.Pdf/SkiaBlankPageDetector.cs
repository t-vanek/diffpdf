using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using SkiaSharp;

namespace DiffPdf.Pdf;

/// <summary>
/// Considers a rendered page blank when the fraction of non-white (inked)
/// pixels is below <see cref="ComparisonOptions.BlankPageThreshold"/>. Works for
/// scanned pages too, since it inspects pixels rather than the text layer.
/// </summary>
public sealed class SkiaBlankPageDetector : IBlankPageDetector
{
    private const int WhiteCutoff = 245; // channel value at/above which a pixel is "white"

    public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options)
    {
        using var bitmap = SKBitmap.Decode(page.Png);
        if (bitmap is null) return false;

        long total = (long)bitmap.Width * bitmap.Height;
        if (total == 0) return true;

        long inked = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if (c.Red < WhiteCutoff || c.Green < WhiteCutoff || c.Blue < WhiteCutoff)
                    inked++;
            }
        }

        return (double)inked / total < options.BlankPageThreshold;
    }
}
