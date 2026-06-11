using System.IO;
using System.Net;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.Services;

/// <summary>Which side a file panel shows.</summary>
public enum BackendKind { Local, Server }

/// <summary>A name collision the user can resolve (overwrite / skip) — both backends throw this for conflicts.</summary>
public sealed class FileBackendConflictException(string message) : Exception(message);

/// <summary>
/// One side of the file manager: the operations a panel and the transfer queue need, with identical
/// semantics on both — listings show folders + PDFs only, folder collisions never merge, conflicts
/// surface as <see cref="FileBackendConflictException"/>. Server paths are root-relative virtual
/// paths; local paths are real absolute Windows paths ("" = the drive list).
/// </summary>
public interface IFileBackend
{
    BackendKind Kind { get; }

    /// <summary>Path a panel opens when it switches to this backend.</summary>
    string DefaultPath { get; }

    /// <summary>False where nothing can be created/uploaded (the local drive list).</summary>
    bool CanWriteTo(string? path);

    string GetParent(string path);

    Task<FileListResponse> ListAsync(string? path, CancellationToken ct = default);
    Task<FileSearchResponse> SearchAsync(string? path, string query, bool recursive, CancellationToken ct = default);
    Task<FileItemDto> CreateFolderAsync(string? parentPath, string folderName, CancellationToken ct = default);

    /// <summary>A non-empty folder without <paramref name="recursive"/> throws <see cref="FileBackendConflictException"/>.</summary>
    Task DeleteAsync(string path, bool recursive, CancellationToken ct = default);

    Task<FileItemDto> RenameAsync(string path, string newName, CancellationToken ct = default);

    /// <summary>Same-backend move into a target folder (name kept). Folder collisions always conflict.</summary>
    Task<FileItemDto> MoveAsync(string sourcePath, string targetDirectory, bool overwrite, CancellationToken ct = default);

    /// <summary>Same-backend copy (folder copy = subfolders + PDFs).</summary>
    Task<FileItemDto> CopyAsync(string sourcePath, string targetDirectory, bool overwrite, CancellationToken ct = default);

    /// <summary>Opens a file for reading — the source half of a cross-backend transfer.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes one PDF into a folder — the sink half of a transfer. An existing name without
    /// <paramref name="overwrite"/> throws <see cref="FileBackendConflictException"/>;
    /// <paramref name="length"/> (when known) drives the 0..1 <paramref name="progress"/> fraction.
    /// </summary>
    Task WriteFileAsync(
        string targetDirectory, string fileName, Stream content, long? length,
        bool overwrite, IProgress<double>? progress, CancellationToken ct = default);
}

/// <summary>The diffpdf server's managed storage, via the typed REST client (409 → conflict exception).</summary>
public sealed class ServerFileBackend(ServerSession session) : IFileBackend
{
    public BackendKind Kind => BackendKind.Server;
    public string DefaultPath => "";

    private DiffPdfClient Client => session.Require();

    public bool CanWriteTo(string? path) => true; // the whole managed tree accepts uploads/folders

    public string GetParent(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? "" : path[..lastSlash];
    }

    public Task<FileListResponse> ListAsync(string? path, CancellationToken ct = default) =>
        Client.ListFilesAsync(path, ct);

    public Task<FileSearchResponse> SearchAsync(string? path, string query, bool recursive, CancellationToken ct = default) =>
        Client.SearchFilesAsync(path, query, recursive, ct);

    public Task<FileItemDto> CreateFolderAsync(string? parentPath, string folderName, CancellationToken ct = default) =>
        Translate(() => Client.CreateFolderAsync(new CreateFolderRequest(parentPath, folderName), ct));

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct = default) =>
        Translate(async () => { await Client.DeleteFileAsync(path, recursive, ct); return true; });

    public Task<FileItemDto> RenameAsync(string path, string newName, CancellationToken ct = default) =>
        Translate(() => Client.RenameFileAsync(new RenameFileRequest(path, newName), ct));

    public Task<FileItemDto> MoveAsync(string sourcePath, string targetDirectory, bool overwrite, CancellationToken ct = default) =>
        Translate(() => Client.MoveFileAsync(new MoveFileRequest(sourcePath, targetDirectory, overwrite), ct));

    public Task<FileItemDto> CopyAsync(string sourcePath, string targetDirectory, bool overwrite, CancellationToken ct = default) =>
        Translate(() => Client.CopyFileAsync(new CopyFileRequest(sourcePath, targetDirectory, overwrite), ct));

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        Client.OpenFileReadAsync(path, ct);

    public async Task WriteFileAsync(
        string targetDirectory, string fileName, Stream content, long? length,
        bool overwrite, IProgress<double>? progress, CancellationToken ct = default)
    {
        // The SDK reports per-file soft errors in the response body — map them to the backend contract.
        var result = await Client.UploadFileAsync(targetDirectory, fileName, content, overwrite, progress, ct);
        if (result.Uploaded) return;
        if (result.ErrorCode == FileUploadErrorCodes.Exists)
            throw new FileBackendConflictException(result.ErrorMessage ?? $"„{fileName}“ na serveru už existuje.");
        throw new IOException(result.ErrorMessage ?? result.ErrorCode ?? "Nahrání selhalo.");
    }

    /// <summary>Maps the server's 409 to the shared conflict exception; other statuses keep their message.</summary>
    private static async Task<T> Translate<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DiffPdfApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new FileBackendConflictException(ex.Detail ?? "Název už existuje.");
        }
    }
}

/// <summary>
/// The user's machine via System.IO. Paths are real absolute paths; "" is a synthetic drive list.
/// Mirrors the server's semantics: listings/search/copy see folders + PDFs only, a folder MOVE
/// relocates everything. Deletes go to the Recycle Bin (volumes without one, e.g. network drives,
/// fall back to a permanent delete — standard shell behaviour); tests turn the bin off.
/// </summary>
public sealed class LocalFileBackend(bool useRecycleBin = true) : IFileBackend
{
    private const int MaxSearchResults = 500;

    public BackendKind Kind => BackendKind.Local;
    public string DefaultPath => "";

    public bool CanWriteTo(string? path) => !string.IsNullOrEmpty(path); // not at the drive list

    public string GetParent(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return Path.GetDirectoryName(path) ?? ""; // a drive root's parent is the drive list
    }

    public Task<FileListResponse> ListAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(ListDrives());

        string full = NormalizeDirectory(path);
        var dir = new DirectoryInfo(full);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Složka neexistuje: {full}");

        var items = new List<FileItemDto>();
        items.AddRange(dir.EnumerateDirectories()
            .Where(d => !IsHiddenOrSystem(d))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto));
        items.AddRange(dir.EnumerateFiles()
            .Where(f => !IsHiddenOrSystem(f) && IsPdfName(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto));

        return Task.FromResult(new FileListResponse
        {
            CurrentPath = full,
            ParentPath = GetParent(full),
            Items = items,
        });
    }

    private static FileListResponse ListDrives() => new()
    {
        CurrentPath = "",
        ParentPath = null,
        Items = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new FileItemDto
            {
                Name = string.IsNullOrWhiteSpace(VolumeLabel(d)) ? d.Name : $"{d.Name}  ({VolumeLabel(d)})",
                Path = d.Name, // e.g. "C:\"
                Kind = FileItemKind.Folder,
                SizeBytes = null,
                LastModified = default, // rendered as "—"
            })
            .ToList(),
    };

    private static string? VolumeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch (IOException) { return null; }
    }

    public Task<FileSearchResponse> SearchAsync(string? path, string query, bool recursive, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("Hledat lze až uvnitř disku nebo složky.");

        string full = NormalizeDirectory(path);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var items = new List<FileItemDto>();
        bool truncated = false;
        foreach (var file in new DirectoryInfo(full).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPdfName(file.Name) || !file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            if (items.Count >= MaxSearchResults) { truncated = true; break; }
            items.Add(ToDto(file));
        }
        items.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(new FileSearchResponse { Query = query, SearchPath = full, Items = items, Truncated = truncated });
    }

    public Task<FileItemDto> CreateFolderAsync(string? parentPath, string folderName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(parentPath))
            throw new InvalidOperationException("Na úrovni disků nelze vytvářet složky.");

        string target = Path.Combine(NormalizeDirectory(parentPath), folderName);
        if (File.Exists(target) || Directory.Exists(target))
            throw new FileBackendConflictException($"„{folderName}“ už existuje.");

        return Task.FromResult(ToDto(Directory.CreateDirectory(target)));
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct = default)
    {
        EnsureNotDriveLevel(path, "Disk nelze smazat.");
        bool isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
            throw new FileNotFoundException($"Položka neexistuje: {path}");

        if (!isFile)
        {
            // The recycle operation is inherently recursive, so the not-empty guard runs FIRST either way.
            var dir = new DirectoryInfo(path);
            if (!recursive && dir.EnumerateFileSystemInfos().Any())
                throw new FileBackendConflictException(
                    $"Složka není prázdná ({dir.EnumerateFileSystemInfos().Count()} položek).");
        }

        if (useRecycleBin)
        {
            RecycleBin.Delete(Path.GetFullPath(path));
        }
        else if (isFile)
        {
            File.Delete(path);
        }
        else
        {
            Directory.Delete(path, recursive: true);
        }
        return Task.CompletedTask;
    }

    public Task<FileItemDto> RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        EnsureNotDriveLevel(path, "Disk nelze přejmenovat.");
        bool isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
            throw new FileNotFoundException($"Položka neexistuje: {path}");

        string oldName = Path.GetFileName(path);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return Task.FromResult(ToDto(Info(path, isFile))); // no-op

        string target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        bool caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            throw new FileBackendConflictException($"„{newName}“ už existuje.");

        if (isFile) File.Move(path, target);
        else Directory.Move(path, target);
        return Task.FromResult(ToDto(Info(target, isFile)));
    }

    public Task<FileItemDto> MoveAsync(string sourcePath, string targetDirectory, bool overwrite, CancellationToken ct = default)
    {
        var (sourceAbs, targetAbs, isFile) = PrepareTransfer(sourcePath, targetDirectory, overwrite);
        if (isFile) File.Move(sourceAbs, targetAbs, overwrite);
        else Directory.Move(sourceAbs, targetAbs); // a move relocates EVERYTHING, hidden content included
        return Task.FromResult(ToDto(Info(targetAbs, isFile)));
    }

    public Task<FileItemDto> CopyAsync(string sourcePath, string targetDirectory, bool overwrite, CancellationToken ct = default)
    {
        var (sourceAbs, targetAbs, isFile) = PrepareTransfer(sourcePath, targetDirectory, overwrite);
        if (isFile) File.Copy(sourceAbs, targetAbs, overwrite);
        else CopyPdfTree(new DirectoryInfo(sourceAbs), targetAbs, ct);
        return Task.FromResult(ToDto(Info(targetAbs, isFile)));
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(path));

    public async Task WriteFileAsync(
        string targetDirectory, string fileName, Stream content, long? length,
        bool overwrite, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!CanWriteTo(targetDirectory))
            throw new InvalidOperationException("Na úrovni disků nelze ukládat soubory.");
        string dir = NormalizeDirectory(targetDirectory);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Cílová složka neexistuje: {dir}");

        string name = Path.GetFileName(fileName);
        string finalPath = Path.Combine(dir, name);
        if (!overwrite && (File.Exists(finalPath) || Directory.Exists(finalPath)))
            throw new FileBackendConflictException($"„{name}“ v cílové složce už existuje.");
        if (Directory.Exists(finalPath))
            throw new FileBackendConflictException($"„{name}“ je složka; souborem ji přepsat nelze.");

        // Same recipe as the server: stream to a temp sibling, atomically move into place.
        string tempPath = Path.Combine(dir, $"~transfer-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await temp.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (progress is not null && length is > 0)
                        progress.Report(Math.Min(1d, (double)total / length.Value));
                }
            }
            File.Move(tempPath, finalPath, overwrite);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { /* best-effort temp cleanup */ }
            throw;
        }
    }

    // ---------------- plumbing ----------------

    /// <summary>Shared move/copy validation mirroring the server's rules (cycle, same-place, collisions).</summary>
    private static (string SourceAbs, string TargetAbs, bool IsFile) PrepareTransfer(
        string sourcePath, string targetDirectory, bool overwrite)
    {
        if (string.IsNullOrEmpty(sourcePath) || IsDriveRoot(sourcePath))
            throw new InvalidOperationException("Disk nelze přesouvat ani kopírovat.");
        if (string.IsNullOrEmpty(targetDirectory))
            throw new InvalidOperationException("Na úroveň disků nelze nic vkládat.");

        string sourceAbs = Path.GetFullPath(sourcePath);
        string targetDir = NormalizeDirectory(targetDirectory);
        bool isFile = File.Exists(sourceAbs);
        if (!isFile && !Directory.Exists(sourceAbs))
            throw new FileNotFoundException($"Položka neexistuje: {sourceAbs}");
        if (!Directory.Exists(targetDir))
            throw new DirectoryNotFoundException($"Cílová složka neexistuje: {targetDir}");

        if (!isFile && (string.Equals(targetDir, sourceAbs, StringComparison.OrdinalIgnoreCase)
                        || targetDir.StartsWith(sourceAbs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Cílová složka leží uvnitř zdrojové.");

        string name = Path.GetFileName(sourceAbs);
        string targetAbs = Path.Combine(targetDir, name);
        if (string.Equals(targetAbs, sourceAbs, StringComparison.OrdinalIgnoreCase))
            throw new FileBackendConflictException("Zdroj a cíl jsou stejné.");

        if (Directory.Exists(targetAbs) || (File.Exists(targetAbs) && (!isFile || !overwrite)))
            throw new FileBackendConflictException(!isFile || Directory.Exists(targetAbs)
                ? $"„{name}“ v cíli už existuje (složky se nikdy neslučují)."
                : $"„{name}“ v cíli už existuje.");

        return (sourceAbs, targetAbs, isFile);
    }

    private static void CopyPdfTree(DirectoryInfo source, string targetAbs, CancellationToken ct)
    {
        Directory.CreateDirectory(targetAbs);
        foreach (var file in source.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsHiddenOrSystem(file) && IsPdfName(file.Name))
                file.CopyTo(Path.Combine(targetAbs, file.Name));
        }
        foreach (var dir in source.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsHiddenOrSystem(dir))
                CopyPdfTree(dir, Path.Combine(targetAbs, dir.Name), ct);
        }
    }

    /// <summary>Canonical directory path; drive roots keep their trailing separator ("C:\").</summary>
    private static string NormalizeDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        return IsDriveRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsDriveRoot(string path) =>
        Path.GetPathRoot(Path.GetFullPath(path)) is { } root
        && string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void EnsureNotDriveLevel(string path, string message)
    {
        if (string.IsNullOrEmpty(path) || IsDriveRoot(path))
            throw new InvalidOperationException(message);
    }

    private static FileSystemInfo Info(string path, bool isFile) =>
        isFile ? new FileInfo(path) : new DirectoryInfo(path);

    private static FileItemDto ToDto(FileSystemInfo info) => new()
    {
        Name = info.Name,
        Path = info.FullName,
        Kind = info is DirectoryInfo ? FileItemKind.Folder : FileItemKind.Pdf,
        SizeBytes = (info as FileInfo)?.Length,
        LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
    };

    private static bool IsHiddenOrSystem(FileSystemInfo info) =>
        (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static bool IsPdfName(string name) =>
        name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && name.Length > 4;
}
