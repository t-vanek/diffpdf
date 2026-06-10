using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Pdf;
using SkiaSharp;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Threshold contract of <see cref="SkiaBlankPageDetector"/> — guards the span-based scan and its
/// early exit against off-by-one drift around <c>BlankPageThreshold</c>.
/// </summary>
public class SkiaBlankPageDetectorTests
{
    private static RenderedPage Page(int width, int height, Action<SKBitmap>? draw = null)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.White);
        draw?.Invoke(bmp);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new RenderedPage { PageNumber = 1, WidthPx = width, HeightPx = height, Dpi = 150, Png = data.ToArray() };
    }

    [Fact]
    public void AllWhitePage_IsBlank()
    {
        Assert.True(new SkiaBlankPageDetector().IsVisuallyBlank(Page(100, 100), new ComparisonOptions()));
    }

    [Fact]
    public void InkedPage_IsNotBlank()
    {
        var page = Page(100, 100, b =>
        {
            using var canvas = new SKCanvas(b);
            canvas.DrawRect(10, 10, 20, 20, new SKPaint { Color = SKColors.Black });
        });
        Assert.False(new SkiaBlankPageDetector().IsVisuallyBlank(page, new ComparisonOptions()));
    }

    [Fact]
    public void ThresholdZero_NeverBlank()
    {
        Assert.False(new SkiaBlankPageDetector().IsVisuallyBlank(
            Page(50, 50), new ComparisonOptions { BlankPageThreshold = 0 }));
    }

    [Fact]
    public void FaintNoiseBelowThreshold_StaysBlank()
    {
        // 1 inked pixel out of 10 000 = 0.0001 < default threshold 0.0002.
        var page = Page(100, 100, b => b.SetPixel(50, 50, SKColors.Black));
        Assert.True(new SkiaBlankPageDetector().IsVisuallyBlank(page, new ComparisonOptions()));

        // Two inked pixels reach the threshold exactly → no longer blank (strict less-than contract).
        var page2 = Page(100, 100, b => { b.SetPixel(50, 50, SKColors.Black); b.SetPixel(51, 50, SKColors.Black); });
        Assert.False(new SkiaBlankPageDetector().IsVisuallyBlank(page2, new ComparisonOptions()));
    }
}
