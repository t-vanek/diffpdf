namespace DiffPdf.Application.Files;

/// <summary>
/// Configures the PDF file manager (the desktop "Správa souborů" page). The manager exposes a single
/// filesystem subtree to clients via virtual relative paths; nothing outside <see cref="RootPath"/>
/// is ever reachable.
/// </summary>
public sealed class FileManagerOptions
{
    public const string SectionName = "FileManager";

    /// <summary>
    /// Root folder of the managed tree (local or UNC). Empty → falls back to <c>ScopeSync:RootPath</c>,
    /// so the manager operates over the same <c>&lt;branch&gt;/&lt;instance&gt;/{old,new,reports}</c> tree the
    /// comparisons use. When neither is configured the file endpoints return 503.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>Per-file upload cap. Also raises the endpoint's request-body limit accordingly.</summary>
    public int MaxUploadSizeMB { get; set; } = 256;

    /// <summary>Reject uploads whose content does not start with <c>%PDF-</c> (cheap spoof check).</summary>
    public bool ValidatePdfMagicBytes { get; set; } = true;

    /// <summary>Result cap for the (phase-2) subtree search endpoint.</summary>
    public int MaxSearchResults { get; set; } = 500;

    public long MaxUploadSizeBytes => MaxUploadSizeMB * 1024L * 1024L;
}
