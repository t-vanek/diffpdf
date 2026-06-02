using System.Text.RegularExpressions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Applies ignore regions / text patterns so dynamic content (timestamps, page
/// numbers, watermarks) does not register as a difference.
/// </summary>
public static class IgnoreFilter
{
    /// <summary>Returns the page with words inside ignore regions or matching ignore patterns removed.</summary>
    public static PageText FilterWords(PageText page, ComparisonOptions options)
    {
        var rects = options.IgnoreRegions
            .Where(r => r.AppliesTo(page.PageNumber))
            .Select(r => r.ToPointsRect(page.Width, page.Height))
            .ToList();

        var regexes = options.IgnoreTextPatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        if (rects.Count == 0 && regexes.Count == 0)
            return page;

        var filtered = page.Words
            .Where(w => !InAnyRect(w.BoundingBox, rects) && !MatchesAny(w.Text, regexes))
            .ToList();

        return page with { Words = filtered };
    }

    /// <summary>Pixel-space (top-left) ignore rectangles for a rendered page.</summary>
    public static IReadOnlyList<RectangleD> PixelRegions(int pageNumber, RenderedPage render, ComparisonOptions options)
    {
        if (options.IgnoreRegions.Count == 0) return [];
        return options.IgnoreRegions
            .Where(r => r.AppliesTo(pageNumber))
            .Select(r => r.ToPixelRect(render.WidthPx, render.HeightPx, render.Dpi))
            .ToList();
    }

    private static bool InAnyRect(RectangleD box, List<RectangleD> rects)
    {
        if (rects.Count == 0) return false;
        double cx = box.X + box.Width / 2.0;
        double cy = box.Y + box.Height / 2.0;
        return rects.Any(r => cx >= r.X && cx <= r.Right && cy >= r.Y && cy <= r.Top);
    }

    private static bool MatchesAny(string text, List<Regex> regexes) =>
        regexes.Count > 0 && regexes.Any(rx => rx.IsMatch(text));
}
