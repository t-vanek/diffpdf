namespace DiffPdf.Core.Models;

/// <summary>Unit in which an <see cref="IgnoreRegion"/> rectangle is expressed.</summary>
public enum IgnoreUnit
{
    /// <summary>Coordinates are fractions (0-1) of the page width/height.</summary>
    Fraction,
    /// <summary>Coordinates are PDF points.</summary>
    Points,
}

/// <summary>
/// A rectangular area excluded from comparison (e.g. a footer carrying a
/// generation timestamp). The rectangle uses a <b>top-left</b> origin, which is
/// the intuitive way to describe "top 8% header" or "bottom 5% footer".
/// </summary>
public sealed record IgnoreRegion
{
    public required RectangleD Area { get; init; }

    public IgnoreUnit Unit { get; init; } = IgnoreUnit.Fraction;

    /// <summary>Page numbers this region applies to; null = every page.</summary>
    public IReadOnlyList<int>? Pages { get; init; }

    /// <summary>Optional human label for reporting.</summary>
    public string? Label { get; init; }

    public bool AppliesTo(int pageNumber) => Pages is null || Pages.Contains(pageNumber);

    /// <summary>Convert to PDF points (bottom-left origin) for a page of the given size.</summary>
    public RectangleD ToPointsRect(double pageWidthPt, double pageHeightPt)
    {
        var (x, y, w, h) = Unit == IgnoreUnit.Fraction
            ? (Area.X * pageWidthPt, Area.Y * pageHeightPt, Area.Width * pageWidthPt, Area.Height * pageHeightPt)
            : (Area.X, Area.Y, Area.Width, Area.Height);

        double yBottom = pageHeightPt - (y + h); // top-left -> bottom-left
        return new RectangleD(x, yBottom, w, h);
    }

    /// <summary>Convert to pixel rectangle (top-left origin) for a render at the given size/DPI.</summary>
    public RectangleD ToPixelRect(int widthPx, int heightPx, int dpi)
    {
        if (Unit == IgnoreUnit.Fraction)
            return new RectangleD(Area.X * widthPx, Area.Y * heightPx, Area.Width * widthPx, Area.Height * heightPx);

        double scale = dpi / 72.0;
        return new RectangleD(Area.X * scale, Area.Y * scale, Area.Width * scale, Area.Height * scale);
    }
}
