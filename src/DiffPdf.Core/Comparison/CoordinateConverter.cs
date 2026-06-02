using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Converts between PDF user space (points, origin bottom-left) and raster
/// pixel space (origin top-left) at a given DPI. Difference regions are stored
/// in PDF points so they are renderer-independent.
/// </summary>
public static class CoordinateConverter
{
    private const double PointsPerInch = 72.0;

    /// <summary>Pixel (top-left origin) rectangle → PDF points (bottom-left origin).</summary>
    public static RectangleD PixelToPoints(RectangleD pixel, int dpi, int pageHeightPx)
    {
        double scale = PointsPerInch / dpi;
        double pageHeightPt = pageHeightPx * scale;
        double widthPt = pixel.Width * scale;
        double heightPt = pixel.Height * scale;
        double xPt = pixel.X * scale;
        double yTopPt = pixel.Y * scale;
        double yBottomPt = pageHeightPt - (yTopPt + heightPt);
        return new RectangleD(xPt, yBottomPt, widthPt, heightPt);
    }

    /// <summary>PDF points (bottom-left origin) → pixel (top-left origin) rectangle.</summary>
    public static RectangleD PointsToPixel(RectangleD points, int dpi, double pageHeightPt)
    {
        double scale = dpi / PointsPerInch;
        double widthPx = points.Width * scale;
        double heightPx = points.Height * scale;
        double xPx = points.X * scale;
        double yTopPx = (pageHeightPt - (points.Y + points.Height)) * scale;
        return new RectangleD(xPx, yTopPx, widthPx, heightPx);
    }
}
