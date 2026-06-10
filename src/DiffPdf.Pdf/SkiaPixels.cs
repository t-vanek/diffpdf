using SkiaSharp;

namespace DiffPdf.Pdf;

/// <summary>
/// Shared pixel-level primitives: decoding PNGs into a guaranteed BGRA8888/unpremul layout so callers can
/// read pixels straight from the buffer (<see cref="SKBitmap.GetPixelSpan"/>) instead of per-pixel
/// <c>GetPixel</c> interop, and the blank-page scan both the comparer and the blank detector share.
/// </summary>
internal static class SkiaPixels
{
    public const int BytesPerPixel = 4; // BGRA8888

    private const int WhiteCutoff = 245; // channel value at/above which a pixel is "white"

    public static SKBitmap? DecodeBgra(byte[] png)
    {
        var bounds = SKBitmap.DecodeBounds(png);
        if (bounds.IsEmpty) return null;
        return SKBitmap.Decode(png, bounds.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Unpremul));
    }

    /// <summary>
    /// True when the fraction of non-white (inked) pixels is below <paramref name="blankThreshold"/>.
    /// Blank ⇔ inked &lt; ceil(threshold·total), so the scan exits as soon as the inked count proves the
    /// page non-blank — typically within the first line of text.
    /// </summary>
    public static bool IsVisuallyBlank(SKBitmap bitmap, double blankThreshold)
    {
        int width = bitmap.Width, height = bitmap.Height;
        long total = (long)width * height;
        if (total == 0) return true;

        long inkedLimit = (long)Math.Ceiling(blankThreshold * total);
        if (inkedLimit <= 0) return false;

        ReadOnlySpan<byte> px = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        long inked = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * BytesPerPixel;
                if (px[i] < WhiteCutoff || px[i + 1] < WhiteCutoff || px[i + 2] < WhiteCutoff)
                {
                    if (++inked >= inkedLimit) return false;
                }
            }
        }

        return true;
    }
}
