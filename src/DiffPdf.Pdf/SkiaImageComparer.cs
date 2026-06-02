using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using SkiaSharp;

namespace DiffPdf.Pdf;

/// <summary>
/// Pixel-level page comparison using SkiaSharp. Differences are clustered into
/// a coarse grid to produce a manageable set of highlight regions, plus an
/// optional diff visualization.
/// </summary>
public sealed class SkiaImageComparer : IImageComparer
{
    private const int MaxRegions = 2000; // safety cap

    public ImageDiffResult Compare(
        byte[] oldPng, byte[] newPng, ComparisonOptions options, IReadOnlyList<RectangleD>? ignoreRegionsPx = null)
    {
        using var oldBmp = SKBitmap.Decode(oldPng);
        using var newBmp = SKBitmap.Decode(newPng);

        if (oldBmp is null || newBmp is null)
            return new ImageDiffResult { DifferenceRatio = 1.0, DifferentPixels = 1, TotalPixels = 1 };

        int width = Math.Max(oldBmp.Width, newBmp.Width);
        int height = Math.Max(oldBmp.Height, newBmp.Height);

        var ignore = ignoreRegionsPx ?? [];

        // Cell size of 1 yields true per-pixel regions.
        int cellSize = Math.Max(1, options.VisualClusterCellSize);
        int cols = (width + cellSize - 1) / cellSize;
        int rows = (height + cellSize - 1) / cellSize;
        var changedCells = new bool[rows, cols];

        long diffPixels = 0;
        long totalPixels = (long)width * height;

        byte tol = options.EffectivePixelTolerance;
        int shift = options.EffectiveShiftTolerance;

        using var diffBmp = new SKBitmap(width, height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor a = x < oldBmp.Width && y < oldBmp.Height ? oldBmp.GetPixel(x, y) : SKColors.White;
                SKColor b = x < newBmp.Width && y < newBmp.Height ? newBmp.GetPixel(x, y) : SKColors.White;

                // Pixels inside an ignore region never count as different.
                // Exact match when PixelTolerance == 0; otherwise per-channel tolerance.
                bool different = !InIgnore(x, y, ignore) && !ColorClose(a, b, tol);

                // Shift tolerance: a differing pixel that is mutually explained by a
                // matching pixel within `shift` px in the other image is just a
                // sub-pixel/anti-aliasing shift, not a real change.
                if (different && shift > 0
                    && NeighborhoodMatch(oldBmp, x, y, shift, b, tol)
                    && NeighborhoodMatch(newBmp, x, y, shift, a, tol))
                {
                    different = false;
                }

                if (different)
                {
                    diffPixels++;
                    changedCells[y / cellSize, x / cellSize] = true;
                    diffBmp.SetPixel(x, y, new SKColor(255, 0, 0, 160));
                }
                else
                {
                    // Faded background so highlights stand out.
                    diffBmp.SetPixel(x, y, Fade(b));
                }
            }
        }

        var regions = ExtractRegions(changedCells, rows, cols, width, height, cellSize);

        byte[]? diffImage = null;
        if (diffPixels > 0)
        {
            using SKData encoded = diffBmp.Encode(SKEncodedImageFormat.Png, 90);
            diffImage = encoded.ToArray();
        }

        return new ImageDiffResult
        {
            DifferentPixels = diffPixels,
            TotalPixels = totalPixels,
            DifferenceRatio = totalPixels == 0 ? 0 : (double)diffPixels / totalPixels,
            Regions = regions,
            DiffImagePng = diffImage,
        };
    }

    private static int Delta(byte a, byte b) => Math.Abs(a - b);

    private static bool ColorClose(SKColor a, SKColor b, byte tol) =>
        Delta(a.Red, b.Red) <= tol && Delta(a.Green, b.Green) <= tol && Delta(a.Blue, b.Blue) <= tol;

    /// <summary>True if any pixel within <paramref name="radius"/> of (x,y) matches <paramref name="target"/>.</summary>
    private static bool NeighborhoodMatch(SKBitmap bmp, int x, int y, int radius, SKColor target, byte tol)
    {
        int x0 = Math.Max(0, x - radius), x1 = Math.Min(bmp.Width - 1, x + radius);
        int y0 = Math.Max(0, y - radius), y1 = Math.Min(bmp.Height - 1, y + radius);
        for (int yy = y0; yy <= y1; yy++)
            for (int xx = x0; xx <= x1; xx++)
                if (ColorClose(bmp.GetPixel(xx, yy), target, tol)) return true;
        return false;
    }

    private static bool InIgnore(int x, int y, IReadOnlyList<RectangleD> rects)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Top) return true;
        }
        return false;
    }

    private static SKColor Fade(SKColor c)
    {
        byte g = (byte)(255 - (255 - (byte)((c.Red + c.Green + c.Blue) / 3)) / 4);
        return new SKColor(g, g, g, 255);
    }

    /// <summary>Merges horizontally adjacent changed cells per row into rectangles.</summary>
    private static List<RectangleD> ExtractRegions(bool[,] cells, int rows, int cols, int width, int height, int cellSize)
    {
        var regions = new List<RectangleD>();
        for (int r = 0; r < rows && regions.Count < MaxRegions; r++)
        {
            int c = 0;
            while (c < cols)
            {
                if (!cells[r, c]) { c++; continue; }
                int start = c;
                while (c < cols && cells[r, c]) c++;

                int x = start * cellSize;
                int y = r * cellSize;
                int w = Math.Min((c - start) * cellSize, width - x);
                int h = Math.Min(cellSize, height - y);
                regions.Add(new RectangleD(x, y, w, h));
            }
        }
        return regions;
    }
}
