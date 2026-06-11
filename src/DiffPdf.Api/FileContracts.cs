using DiffPdf.Application.Files;

namespace DiffPdf.Api;

// ---------------- PDF file manager (Soubory) ----------------
// All paths on this wire are virtual: '/'-separated, relative to the configured FileManager root
// ("" = the root). Absolute server paths never cross this boundary.

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

    public static FileItemDto From(FileSystemEntry e) => new()
    {
        Name = e.Name,
        Path = e.Path,
        Kind = e.IsDirectory ? FileItemKind.Folder : FileItemKind.Pdf,
        SizeBytes = e.SizeBytes,
        LastModified = e.LastModified,
    };
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
/// Per-file outcome of an upload batch. <see cref="ErrorCode"/> is machine-readable
/// (<c>exists</c> | <c>invalid_name</c> | <c>not_pdf</c> | <c>too_large</c> | <c>io_error</c>) —
/// the desktop's overwrite dialog branches on <c>exists</c>; <see cref="ErrorMessage"/> is for humans.
/// </summary>
public sealed record UploadFileResponse
{
    public required string FileName { get; init; }
    public string? Path { get; init; }
    public long SizeBytes { get; init; }
    public required bool Uploaded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static UploadFileResponse From(FileUploadResult r) => new()
    {
        FileName = r.FileName,
        Path = r.Entry?.Path,
        SizeBytes = r.Entry?.SizeBytes ?? 0,
        Uploaded = r.Uploaded,
        ErrorCode = r.ErrorCode,
        ErrorMessage = r.ErrorMessage,
    };
}

/// <summary>Upload batch result — 200 even with per-file failures (clients inspect each file).</summary>
public sealed record UploadFilesResponse
{
    public required IReadOnlyList<UploadFileResponse> Files { get; init; }
    public int SucceededCount => Files.Count(f => f.Uploaded);
}
