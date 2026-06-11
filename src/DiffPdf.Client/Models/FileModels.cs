namespace DiffPdf.Client;

// ---------------- PDF file manager (Správa souborů) ----------------
// Wire mirrors of the server's FileContracts. All paths are virtual: '/'-separated, relative to the
// server's FileManager root ("" = the root); absolute server paths never cross the API boundary.

/// <summary>Kind of a listed item — the manager only knows folders and PDFs (other files are hidden).</summary>
public enum FileItemKind { Folder, Pdf }

/// <summary>One file-manager item.</summary>
public sealed record FileItemDto
{
    public required string Name { get; init; }

    /// <summary>Virtual path of the item, e.g. <c>faktury/2026/smlouva.pdf</c>.</summary>
    public required string Path { get; init; }

    public required FileItemKind Kind { get; init; }

    /// <summary>Null for folders.</summary>
    public long? SizeBytes { get; init; }

    public DateTimeOffset LastModified { get; init; }
}

/// <summary>Folder listing: folders first, then PDFs, both name-sorted.</summary>
public sealed record FileListResponse
{
    /// <summary>Normalized virtual path of the listed folder ("" = root).</summary>
    public required string CurrentPath { get; init; }

    /// <summary>Virtual path of the parent folder; null when <see cref="CurrentPath"/> is the root.</summary>
    public string? ParentPath { get; init; }

    public required IReadOnlyList<FileItemDto> Items { get; init; }
}

/// <summary>Create a folder named <see cref="FolderName"/> inside <see cref="Path"/> ("" = root).</summary>
public sealed record CreateFolderRequest(string? Path, string FolderName);

/// <summary>Rename the item at <see cref="Path"/> to <see cref="NewName"/> (name only, no separators).</summary>
public sealed record RenameFileRequest(string Path, string NewName);

/// <summary>
/// Move <see cref="SourcePath"/> into the folder <see cref="TargetDirectory"/>, keeping its name
/// (TC semantics — combined move+rename is two calls). <see cref="Overwrite"/> applies to files only;
/// folder collisions are always 409 (no merge).
/// </summary>
public sealed record MoveFileRequest(string SourcePath, string TargetDirectory, bool Overwrite = false);

/// <summary>Copy <see cref="SourcePath"/> into <see cref="TargetDirectory"/> (folder copy = subfolders + PDFs).</summary>
public sealed record CopyFileRequest(string SourcePath, string TargetDirectory, bool Overwrite = false);

/// <summary>Subtree search result. <see cref="Truncated"/> = the server's MaxSearchResults cap was hit.</summary>
public sealed record FileSearchResponse
{
    public required string Query { get; init; }
    public required string SearchPath { get; init; }
    public required IReadOnlyList<FileItemDto> Items { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// Per-file outcome of an upload. <see cref="ErrorCode"/> is machine-readable — see
/// <see cref="FileUploadErrorCodes"/>; the overwrite dialog branches on <see cref="FileUploadErrorCodes.Exists"/>.
/// </summary>
public sealed record UploadFileResponse
{
    public required string FileName { get; init; }
    public string? Path { get; init; }
    public long SizeBytes { get; init; }
    public required bool Uploaded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Upload batch result — HTTP 200 even with per-file failures (inspect each file).</summary>
public sealed record UploadFilesResponse
{
    public required IReadOnlyList<UploadFileResponse> Files { get; init; }
}

/// <summary>Machine-readable <see cref="UploadFileResponse.ErrorCode"/> values.</summary>
public static class FileUploadErrorCodes
{
    /// <summary>Target name already exists and overwrite was false — offer to overwrite.</summary>
    public const string Exists = "exists";
    public const string InvalidName = "invalid_name";
    public const string NotPdf = "not_pdf";
    public const string TooLarge = "too_large";
    public const string IoError = "io_error";
}
