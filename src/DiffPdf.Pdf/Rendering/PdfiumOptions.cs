namespace DiffPdf.Pdf.Rendering;

/// <summary>Configuration for the PDFium renderer.</summary>
public sealed class PdfiumOptions
{
    /// <summary>
    /// Hard timeout for a single page render. PDFium's native rasterizer is a blocking call that ignores
    /// cancellation, so the render runs off the request thread and is abandoned (not killed) on expiry.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
