using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using SkiaSharp;

namespace DiffPdf.Pdf;

/// <summary>
/// Pixel-level page comparison using SkiaSharp. Differences are clustered into
/// a coarse grid to produce a manageable set of highlight regions. Pixels are
/// read directly from the decoded BGRA buffers; byte-identical rows short-circuit,
/// so an unchanged page costs one sequence comparison per row instead of per-pixel work.
/// </summary>
public sealed class SkiaImageComparer : IImageComparer
{
    private const int MaxRegions = 2000; // safety cap
    private const int Bpp = 4;           // BGRA8888

    public ImageDiffResult Compare(
        byte[] oldPng, byte[] newPng, ComparisonOptions options, IReadOnlyList<RectangleD>? ignoreRegionsPx = null)
    {
        using var oldBmp = SkiaPixels.DecodeBgra(oldPng);
        using var newBmp = SkiaPixels.DecodeBgra(newPng);

        if (oldBmp is null || newBmp is null)
            return new ImageDiffResult { DifferenceRatio = 1.0, DifferentPixels = 1, TotalPixels = 1 };

        int oldW = oldBmp.Width, oldH = oldBmp.Height;
        int newW = newBmp.Width, newH = newBmp.Height;
        int width = Math.Max(oldW, newW);
        int height = Math.Max(oldH, newH);

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

        ReadOnlySpan<byte> oldPx = oldBmp.GetPixelSpan();
        ReadOnlySpan<byte> newPx = newBmp.GetPixelSpan();
        int oldStride = oldBmp.RowBytes, newStride = newBmp.RowBytes;
        bool sameWidth = oldW == newW;
        int rowBytes = oldW * Bpp;

        for (int y = 0; y < height; y++)
        {
            bool hasOld = y < oldH, hasNew = y < newH;

            // Byte-identical rows (the dominant case on unchanged pages) cannot contain a difference
            // under any tolerance, ignore set or shift — skip the per-pixel pass for the whole row.
            if (sameWidth && hasOld && hasNew
                && oldPx.Slice(y * oldStride, rowBytes).SequenceEqual(newPx.Slice(y * newStride, rowBytes)))
            {
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                // The area beyond the smaller bitmap compares as white.
                byte aB = 255, aG = 255, aR = 255;
                if (hasOld && x < oldW)
                {
                    int i = y * oldStride + x * Bpp;
                    aB = oldPx[i]; aG = oldPx[i + 1]; aR = oldPx[i + 2];
                }
                byte bB = 255, bG = 255, bR = 255;
                if (hasNew && x < newW)
                {
                    int i = y * newStride + x * Bpp;
                    bB = newPx[i]; bG = newPx[i + 1]; bR = newPx[i + 2];
                }

                // Exact match when PixelTolerance == 0; otherwise per-channel tolerance.
                // Pixels inside an ignore region never count as different.
                bool different = !ColorClose(aR, aG, aB, bR, bG, bB, tol) && !InIgnore(x, y, ignore);

                // Shift tolerance: a differing pixel that is mutually explained by a
                // matching pixel within `shift` px in the other image is just a
                // sub-pixel/anti-aliasing shift, not a real change.
                if (different && shift > 0
                    && NeighborhoodMatch(oldPx, oldStride, oldW, oldH, x, y, shift, bR, bG, bB, tol)
                    && NeighborhoodMatch(newPx, newStride, newW, newH, x, y, shift, aR, aG, aB, tol))
                {
                    different = false;
                }

                if (different)
                {
                    diffPixels++;
                    changedCells[y / cellSize, x / cellSize] = true;
                }
            }
        }

        var regions = ExtractRegions(changedCells, rows, cols, width, height, cellSize);

        return new ImageDiffResult
        {
            DifferentPixels = diffPixels,
            TotalPixels = totalPixels,
            DifferenceRatio = totalPixels == 0 ? 0 : (double)diffPixels / totalPixels,
            Regions = regions,
            // Blankness rides on the decode this comparison already paid for — the engine reuses these
            // instead of re-decoding both PNGs in the standalone blank detector.
            OldBlank = SkiaPixels.IsVisuallyBlank(oldBmp, options.BlankPageThreshold),
            NewBlank = SkiaPixels.IsVisuallyBlank(newBmp, options.BlankPageThreshold),
        };
    }

    private static int Delta(byte a, byte b) => Math.Abs(a - b);

    private static bool ColorClose(byte aR, byte aG, byte aB, byte bR, byte bG, byte bB, byte tol) =>
        Delta(aR, bR) <= tol && Delta(aG, bG) <= tol && Delta(aB, bB) <= tol;

    /// <summary>True if any pixel within <paramref name="radius"/> of (x,y) matches the target colour.</summary>
    private static bool NeighborhoodMatch(
        ReadOnlySpan<byte> px, int stride, int width, int height,
        int x, int y, int radius, byte tR, byte tG, byte tB, byte tol)
    {
        int x0 = Math.Max(0, x - radius), x1 = Math.Min(width - 1, x + radius);
        int y0 = Math.Max(0, y - radius), y1 = Math.Min(height - 1, y + radius);
        for (int yy = y0; yy <= y1; yy++)
        {
            int row = yy * stride;
            for (int xx = x0; xx <= x1; xx++)
            {
                int i = row + xx * Bpp;
                if (ColorClose(px[i + 2], px[i + 1], px[i], tR, tG, tB, tol)) return true;
            }
        }
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
