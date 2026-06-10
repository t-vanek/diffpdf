namespace DiffPdf.Pdf.Rendering;

/// <summary>Configuration for the PDFium renderer.</summary>
public sealed class PdfiumOptions
{
    /// <summary>
    /// Hard timeout for a single page render. PDFium's native rasterizer is a blocking call that ignores
    /// cancellation, so the render runs off the request thread and is abandoned (not killed) on expiry.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Budget (bytes, per process) for the in-memory PDF document cache. PDFtoImage needs the whole file as
    /// a byte array for every page render, so without a cache an N-page comparison re-reads each document
    /// N times — painful for large PDFs on UNC/CIFS shares. Entries are validated against file length and
    /// last-write time on every hit; files larger than the budget are read through without being cached.
    /// Set ≤ 0 to disable caching entirely.
    /// </summary>
    public long DocumentCacheBytes { get; set; } = 256L * 1024 * 1024;
}
