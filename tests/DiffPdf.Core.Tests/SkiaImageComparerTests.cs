using DiffPdf.Core.Models;
using DiffPdf.Pdf;
using SkiaSharp;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Pixel-exact contract of <see cref="SkiaImageComparer"/>: tolerance, shift tolerance, ignore
/// regions, size mismatch (white padding) and clustering — guards the span-based pixel path
/// against colour-layout regressions (channel order, stride, alpha).
/// </summary>
public class SkiaImageComparerTests
{
    private static byte[] Png(int width, int height, Action<SKBitmap>? draw = null)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.White);
        draw?.Invoke(bmp);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ComparisonOptions Exact => new() { PixelTolerance = 0, ShiftTolerance = 0 };

    [Fact]
    public void IdenticalImages_ReportNoDifference()
    {
        var comparer = new SkiaImageComparer();
        var result = comparer.Compare(
            Png(60, 40, b => b.Erase(new SKColor(10, 120, 240))),
            Png(60, 40, b => b.Erase(new SKColor(10, 120, 240))),
            Exact);

        Assert.Equal(0, result.DifferentPixels);
        Assert.Equal(0, result.DifferenceRatio);
        Assert.Empty(result.Regions);
        Assert.Equal(60L * 40, result.TotalPixels);
    }

    [Fact]
    public void SingleChangedPixel_IsDetectedAndClustered()
    {
        var result = new SkiaImageComparer().Compare(
            Png(60, 40),
            Png(60, 40, b => b.SetPixel(10, 10, SKColors.Black)),
            Exact);

        Assert.Equal(1, result.DifferentPixels);
        var region = Assert.Single(result.Regions);
        Assert.True(region.X <= 10 && region.Right >= 10, "region must cover the changed pixel column");
        Assert.True(region.Y <= 10 && region.Top >= 10, "region must cover the changed pixel row");
    }

    [Fact]
    public void PixelTolerance_SwallowsSmallChannelDelta()
    {
        var oldPng = Png(20, 20, b => b.Erase(new SKColor(200, 200, 200)));
        var newPng = Png(20, 20, b => b.Erase(new SKColor(205, 203, 201)));
        var comparer = new SkiaImageComparer();

        Assert.Equal(0, comparer.Compare(oldPng, newPng,
            new ComparisonOptions { PixelTolerance = 5, ShiftTolerance = 0 }).DifferentPixels);
        Assert.Equal(20L * 20, comparer.Compare(oldPng, newPng,
            new ComparisonOptions { PixelTolerance = 4, ShiftTolerance = 0 }).DifferentPixels);
    }

    [Fact]
    public void ShiftTolerance_AbsorbsOnePixelShift()
    {
        var oldPng = Png(30, 30, b => b.SetPixel(10, 10, SKColors.Black));
        var newPng = Png(30, 30, b => b.SetPixel(11, 10, SKColors.Black));
        var comparer = new SkiaImageComparer();

        Assert.Equal(0, comparer.Compare(oldPng, newPng,
            new ComparisonOptions { PixelTolerance = 0, ShiftTolerance = 1 }).DifferentPixels);
        Assert.Equal(2, comparer.Compare(oldPng, newPng,
            new ComparisonOptions { PixelTolerance = 0, ShiftTolerance = 0 }).DifferentPixels);
    }

    [Fact]
    public void IgnoreRegion_SuppressesDifferences()
    {
        var oldPng = Png(40, 40);
        var newPng = Png(40, 40, b =>
        {
            for (int x = 5; x <= 8; x++)
                for (int y = 5; y <= 8; y++)
                    b.SetPixel(x, y, SKColors.Black);
        });

        var ignored = new SkiaImageComparer().Compare(oldPng, newPng, Exact, [new RectangleD(5, 5, 3, 3)]);
        Assert.Equal(0, ignored.DifferentPixels);

        var flagged = new SkiaImageComparer().Compare(oldPng, newPng, Exact);
        Assert.Equal(16, flagged.DifferentPixels);
    }

    [Fact]
    public void SizeMismatch_ComparesExtraAreaAgainstWhite()
    {
        var comparer = new SkiaImageComparer();

        // The extra strip is white → still identical.
        var widened = comparer.Compare(Png(40, 40), Png(50, 40), Exact);
        Assert.Equal(0, widened.DifferentPixels);
        Assert.Equal(50L * 40, widened.TotalPixels);

        // Ink inside the extra strip differs from the implicit white.
        var inked = comparer.Compare(Png(40, 40), Png(50, 40, b => b.SetPixel(45, 10, SKColors.Black)), Exact);
        Assert.Equal(1, inked.DifferentPixels);
    }

    [Fact]
    public void UndecodableImage_ReportsFullDifference()
    {
        var result = new SkiaImageComparer().Compare([1, 2, 3], Png(10, 10), Exact);
        Assert.Equal(1.0, result.DifferenceRatio);
        Assert.Equal(1, result.DifferentPixels);
    }

    [Fact]
    public void BlanknessOfBothSides_RidesOnTheDiffDecode()
    {
        // 20×20 px of ink on 100×100 (4 %) is far above the default 0.02 % blank threshold.
        var inked = Png(100, 100, b =>
        {
            using var canvas = new SKCanvas(b);
            canvas.DrawRect(10, 10, 20, 20, new SKPaint { Color = SKColors.Black });
        });

        var result = new SkiaImageComparer().Compare(Png(100, 100), inked, new ComparisonOptions());

        Assert.True(result.OldBlank);
        Assert.False(result.NewBlank);
    }
}
