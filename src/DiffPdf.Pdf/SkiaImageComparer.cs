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
    private const int CellSize = 24;     // grid resolution for clustering (px)
    private const int MaxRegions = 1000; // safety cap

    public ImageDiffResult Compare(byte[] oldPng, byte[] newPng, ComparisonOptions options)
    {
        using var oldBmp = SKBitmap.Decode(oldPng);
        using var newBmp = SKBitmap.Decode(newPng);

        if (oldBmp is null || newBmp is null)
            return new ImageDiffResult { DifferenceRatio = 1.0 };

        int width = Math.Max(oldBmp.Width, newBmp.Width);
        int height = Math.Max(oldBmp.Height, newBmp.Height);

        int cols = (width + CellSize - 1) / CellSize;
        int rows = (height + CellSize - 1) / CellSize;
        var changedCells = new bool[rows, cols];

        long diffPixels = 0;
        long totalPixels = (long)width * height;

        using var diffBmp = new SKBitmap(width, height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor a = x < oldBmp.Width && y < oldBmp.Height ? oldBmp.GetPixel(x, y) : SKColors.White;
                SKColor b = x < newBmp.Width && y < newBmp.Height ? newBmp.GetPixel(x, y) : SKColors.White;

                bool different = Delta(a.Red, b.Red) > options.PixelTolerance
                    || Delta(a.Green, b.Green) > options.PixelTolerance
                    || Delta(a.Blue, b.Blue) > options.PixelTolerance;

                if (different)
                {
                    diffPixels++;
                    changedCells[y / CellSize, x / CellSize] = true;
                    diffBmp.SetPixel(x, y, new SKColor(255, 0, 0, 160));
                }
                else
                {
                    // Faded background so highlights stand out.
                    diffBmp.SetPixel(x, y, Fade(b));
                }
            }
        }

        var regions = ExtractRegions(changedCells, rows, cols, width, height);

        byte[]? diffImage = null;
        if (diffPixels > 0)
        {
            using SKData encoded = diffBmp.Encode(SKEncodedImageFormat.Png, 90);
            diffImage = encoded.ToArray();
        }

        return new ImageDiffResult
        {
            DifferenceRatio = totalPixels == 0 ? 0 : (double)diffPixels / totalPixels,
            Regions = regions,
            DiffImagePng = diffImage,
        };
    }

    private static int Delta(byte a, byte b) => Math.Abs(a - b);

    private static SKColor Fade(SKColor c)
    {
        byte g = (byte)(255 - (255 - (byte)((c.Red + c.Green + c.Blue) / 3)) / 4);
        return new SKColor(g, g, g, 255);
    }

    /// <summary>Merges horizontally adjacent changed cells per row into rectangles.</summary>
    private static List<RectangleD> ExtractRegions(bool[,] cells, int rows, int cols, int width, int height)
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

                int x = start * CellSize;
                int y = r * CellSize;
                int w = Math.Min((c - start) * CellSize, width - x);
                int h = Math.Min(CellSize, height - y);
                regions.Add(new RectangleD(x, y, w, h));
            }
        }
        return regions;
    }
}
