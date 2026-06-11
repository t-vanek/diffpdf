namespace DiffPdf.Application.Files;

/// <summary>Outcome of a file-manager operation; the endpoint maps it to an HTTP status.</summary>
public enum FileOpStatus
{
    Ok,
    /// <summary>Neither FileManager:RootPath nor ScopeSync:RootPath is configured (503).</summary>
    RootNotConfigured,
    /// <summary>Virtual path failed validation — traversal, rooted, invalid characters (400).</summary>
    InvalidPath,
    /// <summary>A file/folder name failed validation (400).</summary>
    InvalidName,
    /// <summary>The addressed item does not exist (404).</summary>
    NotFound,
    /// <summary>Name collision, or a non-empty folder without recursive (409).</summary>
    Conflict,
    /// <summary>The path addresses a folder where a file is required (400).</summary>
    NotAFile,
    /// <summary>The path addresses a file where a folder is required (400).</summary>
    NotAFolder,
}

/// <summary>One listed item. <see cref="Path"/> is the virtual path relative to the manager root.</summary>
public sealed record FileSystemEntry(string Name, string Path, bool IsDirectory, long? SizeBytes, DateTimeOffset LastModified);

/// <summary>Folder listing: directories first, then PDF files, both name-sorted. Other files are hidden.</summary>
public sealed record FileListResult(
    FileOpStatus Status, string CurrentPath = "", string? ParentPath = null, IReadOnlyList<FileSystemEntry>? Items = null);

/// <summary>Result of an operation that addresses a single entry. <see cref="Detail"/> refines the generic status message.</summary>
public sealed record FileEntryResult(FileOpStatus Status, FileSystemEntry? Entry = null, string? Detail = null);

/// <summary><see cref="ItemCount"/> is the folder's top-level item count when a non-recursive delete hits a non-empty folder.</summary>
public sealed record FileDeleteResult(FileOpStatus Status, int? ItemCount = null);

/// <summary>Resolved download target; <see cref="AbsolutePath"/> never leaves the service layer's host.</summary>
public sealed record FileDownloadResult(FileOpStatus Status, string? AbsolutePath = null, string? FileName = null);

/// <summary>Subtree search outcome. <see cref="Truncated"/> = the MaxSearchResults cap cut the result off.</summary>
public sealed record FileSearchResult(
    FileOpStatus Status, string SearchPath = "", string Query = "",
    IReadOnlyList<FileSystemEntry>? Items = null, bool Truncated = false, string? Detail = null);

/// <summary>
/// Diagnostics of the file-manager storage: whether a root is configured (and from which appsettings
/// key it resolved), whether it is reachable and writable, the volume's free space and the effective
/// limits. The root path itself is included — this is an authenticated admin diagnostic, same as the
/// scope root endpoint.
/// </summary>
public sealed record FileManagerStatus
{
    public required bool Configured { get; init; }

    /// <summary>Which configuration key supplied the root: "FileManager:RootPath" or "ScopeSync:RootPath"; null when unconfigured.</summary>
    public string? ResolvedFrom { get; init; }

    public string? RootPath { get; init; }

    /// <summary>The root folder exists (it is auto-created on first use, so this probe creates it too).</summary>
    public bool RootExists { get; init; }

    /// <summary>A probe file could be created and deleted inside the root.</summary>
    public bool Writable { get; init; }

    public long? FreeSpaceBytes { get; init; }
    public long? TotalSpaceBytes { get; init; }

    public int MaxUploadSizeMB { get; init; }
    public bool ValidatePdfMagicBytes { get; init; }
    public int MaxSearchResults { get; init; }

    /// <summary>Human-readable detail when the root is missing or not writable.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Per-file upload outcome. Soft — one bad file must not fail the batch, so failures are carried as
/// <see cref="ErrorCode"/> (machine: drives the client's overwrite dialog) + <see cref="ErrorMessage"/> (human).
/// </summary>
public sealed record FileUploadResult(string FileName, FileSystemEntry? Entry = null, string? ErrorCode = null, string? ErrorMessage = null)
{
    public bool Uploaded => Entry is not null;
}

/// <summary>Machine-readable per-file upload error codes (wire contract — the desktop branches on these).</summary>
public static class FileUploadErrorCodes
{
    /// <summary>Target name already exists and overwrite was false — the client offers to overwrite.</summary>
    public const string Exists = "exists";
    public const string InvalidName = "invalid_name";
    public const string NotPdf = "not_pdf";
    public const string TooLarge = "too_large";
    public const string IoError = "io_error";
}
