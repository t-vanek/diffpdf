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

    /// <summary>Hard timeout for a single page render.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
