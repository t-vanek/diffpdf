namespace DiffPdf.Pdf.Rendering;

/// <summary>Configuration for the Ghostscript renderer.</summary>
public sealed class GhostscriptOptions
{
    /// <summary>
    /// Path to the Ghostscript executable. Defaults to "gs" (resolved via PATH);
    /// on Windows this is typically "gswin64c".
    /// </summary>
    public string ExecutablePath { get; set; } =
        Environment.GetEnvironmentVariable("GHOSTSCRIPT_PATH") ?? "gs";

    /// <summary>Hard timeout for a single page render. A block render (see <see cref="RenderBlockSize"/>)
    /// scales this by the block size.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many consecutive pages one Ghostscript invocation renders. Ghostscript re-parses the whole
    /// document on every spawn, so per-page invocation costs O(pages × document size); a block amortizes
    /// the parse across up to this many pages and parks the byproducts in the rendered-page cache until
    /// the engine asks for them. <c>1</c> = one spawn per page (the pre-block behaviour); clamped to 32.
    /// </summary>
    public int RenderBlockSize { get; set; } = 8;

    /// <summary>
    /// Budget (bytes, per process) for the rendered-page PNG cache holding block-render byproducts. It is a
    /// short-lived prefetch buffer — pages wait seconds until the engine consumes them — so a modest budget
    /// suffices. ≤ 0 disables the cache and with it block rendering (every page renders individually).
    /// </summary>
    public long RenderCacheBytes { get; set; } = 128L * 1024 * 1024;
}
