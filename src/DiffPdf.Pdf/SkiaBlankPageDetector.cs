using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Pdf;

/// <summary>
/// Considers a rendered page blank when the fraction of non-white (inked)
/// pixels is below <see cref="ComparisonOptions.BlankPageThreshold"/>. Works for
/// scanned pages too, since it inspects pixels rather than the text layer.
/// The scan itself lives in <see cref="SkiaPixels"/> and is shared with
/// <see cref="SkiaImageComparer"/>, which evaluates blankness on the decode it
/// already paid for — this standalone detector serves the render-only paths
/// (added/removed pages, blank detection without a visual diff).
/// </summary>
public sealed class SkiaBlankPageDetector : IBlankPageDetector
{
    public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options)
    {
        using var bitmap = SkiaPixels.DecodeBgra(page.Png);
        if (bitmap is null) return false;

        return SkiaPixels.IsVisuallyBlank(bitmap, options.BlankPageThreshold);
    }
}
