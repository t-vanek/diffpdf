using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Storage;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Network;
using Microsoft.Extensions.Options;

namespace DiffPdf.Application.Files;

/// <summary>
/// PDF file manager over a single configured filesystem subtree (the desktop "Správa souborů" page).
/// Lists folders and PDFs; destructive operations protect the configured branch/instance structure.
/// All paths crossing this boundary are virtual ('/'-separated, root-relative) and are resolved via
/// <see cref="VirtualPath"/>, so no operation can address anything outside the root. Listing shows
/// folders and <c>*.pdf</c> only, but delete/conflict semantics see the folder's real content.
/// </summary>
public interface IFileManagerService
{
    FileListResult List(string? path);

    /// <summary>Validates that <paramref name="path"/> addresses an existing folder (upload pre-flight).</summary>
    FileEntryResult ValidateDirectory(string? path);

    /// <summary>
    /// Stores one uploaded PDF into <paramref name="directoryPath"/>. Failures are per-file soft errors
    /// (<see cref="FileUploadResult.ErrorCode"/>); the content is written to a temp file and atomically
    /// moved into place, so a crashed upload never leaves a partial PDF under the target name.
    /// </summary>
    Task<FileUploadResult> UploadAsync(string? directoryPath, string clientFileName, Stream content, bool overwrite, CancellationToken ct = default);

    FileEntryResult CreateFolder(string? parentPath, string? folderName);

    FileDeleteResult Delete(string? path, bool recursive);

    FileEntryResult Rename(string? path, string? newName);

    /// <summary>
    /// Moves a file or folder into <paramref name="targetDirectory"/>, keeping its name (TC semantics).
    /// <paramref name="overwrite"/> applies to file→file only; a folder name collision is always a conflict
    /// (no merge). A move relocates the folder's ENTIRE content, including files the listing hides.
    /// </summary>
    FileEntryResult Move(string? sourcePath, string? targetDirectory, bool overwrite);

    /// <summary>
    /// Copies a file or folder into <paramref name="targetDirectory"/>, keeping its name. A folder copy
    /// replicates what the manager shows — subfolders and PDFs; hidden/system/non-PDF files stay behind.
    /// </summary>
    FileEntryResult Copy(string? sourcePath, string? targetDirectory, bool overwrite, CancellationToken ct = default);

    /// <summary>Finds PDFs whose name contains <paramref name="query"/> (case-insensitive) under <paramref name="path"/>.</summary>
    FileSearchResult Search(string? path, string? query, bool recursive, CancellationToken ct = default);

    FileDownloadResult ResolveDownload(string? path);

    /// <summary>Storage diagnostics for the client's configuration page (root, availability, limits).</summary>
    FileManagerStatus GetStatus();
}

public sealed class FileManagerService(
    IOptions<FileManagerOptions> options,
    IOptions<ScopeSyncOptions> scopeSync,
    INetworkShareResolver? networkResolver = null,
    INetworkShareConnector? networkConnector = null) : IFileManagerService, IDisposable
{
    private const string PdfExtension = ".pdf";
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    private readonly FileManagerOptions _options = options.Value;
    private readonly ScopeSyncOptions _scopeSync = scopeSync.Value;
    private readonly Dictionary<(string Path, string? Profile), NetworkShareConnection> _connections = [];

    /// <summary>The effective root (FileManager:RootPath, else ScopeSync:RootPath), or null when unconfigured.</summary>
    private string? Root
    {
        get
        {
            string? root = !string.IsNullOrWhiteSpace(_options.RootPath) ? _options.RootPath : _scopeSync.RootPath;
            return string.IsNullOrWhiteSpace(root) ? null : ConnectRoot(root,
                string.IsNullOrWhiteSpace(_options.RootPath) ? _scopeSync.CredentialProfile : _options.CredentialProfile);
        }
    }

    private string ConnectRoot(string root, string? profile)
    {
        if (_connections.TryGetValue((root, profile), out var existing)) return existing.Path;
        var resolved = networkResolver?.Resolve(root, credentialProfile: profile);
        var connection = networkConnector?.Connect(resolved?.Path ?? root, resolved?.Credentials)
            ?? new NetworkShareConnection(resolved?.Path ?? root);
        _connections.Add((root, profile), connection);
        return connection.Path;
    }

    // The scoped service keeps authenticated shares/mounts alive through streaming HTTP responses.
    public void Dispose()
    {
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();
    }

    public FileListResult List(string? path)
    {
        var (status, abs, normalized) = Resolve(path);
        if (status is not FileOpStatus.Ok) return new FileListResult(status);
        if (File.Exists(abs)) return new FileListResult(FileOpStatus.NotAFolder);
        if (!Directory.Exists(abs)) return new FileListResult(FileOpStatus.NotFound);

        var dir = new DirectoryInfo(abs!);
        var items = new List<FileSystemEntry>();

        items.AddRange(dir.EnumerateDirectories()
            .Where(d => !IsHiddenOrSystem(d))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => ToEntry(d, normalized!)));

        // Enumerate all files and filter the extension ourselves — the Win32 "*.pdf" pattern has
        // legacy 8.3 quirks (it can match "x.pdfx"); an explicit comparison has none.
        items.AddRange(dir.EnumerateFiles()
            .Where(f => !IsHiddenOrSystem(f) && IsPdfName(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => ToEntry(f, normalized!)));

        string? parent = normalized!.Length == 0 ? null : VirtualPath.GetParent(normalized);
        return new FileListResult(FileOpStatus.Ok, normalized, parent, items);
    }

    public FileEntryResult ValidateDirectory(string? path)
    {
        var (status, abs, normalized) = Resolve(path);
        if (status is not FileOpStatus.Ok) return new FileEntryResult(status);
        if (File.Exists(abs)) return new FileEntryResult(FileOpStatus.NotAFolder);
        if (!Directory.Exists(abs)) return new FileEntryResult(FileOpStatus.NotFound);

        var dir = new DirectoryInfo(abs!);
        var entry = normalized!.Length == 0
            ? new FileSystemEntry("", "", true, null, new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero))
            : ToEntry(dir, VirtualPath.GetParent(normalized));
        return new FileEntryResult(FileOpStatus.Ok, entry);
    }

    public async Task<FileUploadResult> UploadAsync(
        string? directoryPath, string clientFileName, Stream content, bool overwrite, CancellationToken ct = default)
    {
        // Browsers/clients may send a full client-side path — only the trailing name matters here.
        string name = Path.GetFileName(clientFileName ?? string.Empty).Trim();

        var (status, absDir, normalizedDir) = Resolve(directoryPath);
        if (status is not FileOpStatus.Ok || !Directory.Exists(absDir))
            return Soft(name, FileUploadErrorCodes.IoError, "Target folder is not available.");

        if (!VirtualPath.IsValidName(name))
            return Soft(name, FileUploadErrorCodes.InvalidName, $"Invalid file name: '{name}'.");
        if (!IsPdfName(name))
            return Soft(name, FileUploadErrorCodes.NotPdf, "Only .pdf files are accepted.");

        string finalPath = Path.Combine(absDir!, name);
        if (FileSystemPathSafety.ContainsLink(finalPath))
            return Soft(name, FileUploadErrorCodes.InvalidName, "Symbolic links and junctions are not supported.");
        if (!overwrite && (File.Exists(finalPath) || Directory.Exists(finalPath)))
            return Soft(name, FileUploadErrorCodes.Exists, $"'{name}' already exists.");
        if (Directory.Exists(finalPath))
            return Soft(name, FileUploadErrorCodes.Exists, $"'{name}' is a folder; it cannot be overwritten by a file.");

        // Stream to a temp file next to the target (same volume → File.Move is atomic), validating the
        // size cap and the %PDF- magic on the way; only a fully written, valid PDF gets the real name.
        string tempPath = Path.Combine(absDir!, $"~upload-{Guid.NewGuid():N}.tmp");
        long totalBytes = 0;
        try
        {
            byte[] header = new byte[PdfMagic.Length];
            int headerFilled = 0;

            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > _options.MaxUploadSizeBytes)
                        return await AbortAsync(temp, tempPath, name, FileUploadErrorCodes.TooLarge,
                            $"File exceeds the {_options.MaxUploadSizeMB} MB upload limit.");

                    if (headerFilled < header.Length)
                    {
                        int take = Math.Min(header.Length - headerFilled, read);
                        Array.Copy(buffer, 0, header, headerFilled, take);
                        headerFilled += take;
                    }
                    await temp.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (totalBytes == 0)
                return Cleanup(tempPath, Soft(name, FileUploadErrorCodes.NotPdf, "The uploaded file is empty."));
            if (_options.ValidatePdfMagicBytes && (headerFilled < header.Length || !header.AsSpan().SequenceEqual(PdfMagic)))
                return Cleanup(tempPath, Soft(name, FileUploadErrorCodes.NotPdf, "The content is not a PDF (missing %PDF- header)."));

            try
            {
                File.Move(tempPath, finalPath, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(finalPath))
            {
                // Lost a create race after the pre-check — same answer as if the file had been there all along.
                return Cleanup(tempPath, Soft(name, FileUploadErrorCodes.Exists, $"'{name}' already exists."));
            }

            return new FileUploadResult(name, ToEntry(new FileInfo(finalPath), normalizedDir!));
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return Soft(name, FileUploadErrorCodes.IoError, ex.Message);
        }
    }

    public FileEntryResult CreateFolder(string? parentPath, string? folderName)
    {
        var (status, absParent, normalizedParent) = Resolve(parentPath);
        if (status is not FileOpStatus.Ok) return new FileEntryResult(status);
        if (File.Exists(absParent)) return new FileEntryResult(FileOpStatus.NotAFolder);
        if (!Directory.Exists(absParent)) return new FileEntryResult(FileOpStatus.NotFound);

        string name = folderName?.Trim() ?? string.Empty;
        if (!VirtualPath.IsValidName(name))
            return new FileEntryResult(FileOpStatus.InvalidName, Detail: $"Invalid folder name: '{name}'.");

        string target = Path.Combine(absParent!, name);
        if (FileSystemPathSafety.ContainsLink(target)) return new FileEntryResult(FileOpStatus.InvalidPath);
        if (File.Exists(target) || Directory.Exists(target))
            return new FileEntryResult(FileOpStatus.Conflict, Detail: $"'{name}' already exists.");

        var created = Directory.CreateDirectory(target);
        return new FileEntryResult(FileOpStatus.Ok, ToEntry(created, normalizedParent!));
    }

    public FileDeleteResult Delete(string? path, bool recursive)
    {
        var (status, abs, normalized) = Resolve(path);
        if (status is not FileOpStatus.Ok) return new FileDeleteResult(status);
        if (normalized!.Length == 0) return new FileDeleteResult(FileOpStatus.InvalidPath); // the root itself

        if (File.Exists(abs))
        {
            File.Delete(abs!);
            return new FileDeleteResult(FileOpStatus.Ok);
        }
        if (!Directory.Exists(abs)) return new FileDeleteResult(FileOpStatus.NotFound);
        if (IsManagedDirectory(abs!)) return new FileDeleteResult(FileOpStatus.ProtectedLocation);

        var dir = new DirectoryInfo(abs!);
        // The conflict check sees ALL content (including files the PDF-only listing hides) — silently
        // deleting invisible items would be worse than asking for an explicit recursive confirmation.
        if (!recursive && dir.EnumerateFileSystemInfos().Any())
            return new FileDeleteResult(FileOpStatus.Conflict, dir.EnumerateFileSystemInfos().Count());

        Directory.Delete(abs!, recursive: true);
        return new FileDeleteResult(FileOpStatus.Ok);
    }

    public FileEntryResult Rename(string? path, string? newName)
    {
        var (status, abs, normalized) = Resolve(path);
        if (status is not FileOpStatus.Ok) return new FileEntryResult(status);
        if (normalized!.Length == 0) return new FileEntryResult(FileOpStatus.InvalidPath); // the root itself

        bool isFile = File.Exists(abs);
        if (!isFile && !Directory.Exists(abs)) return new FileEntryResult(FileOpStatus.NotFound);
        if (!isFile && IsManagedDirectory(abs!)) return new FileEntryResult(FileOpStatus.ProtectedLocation);

        string name = newName?.Trim() ?? string.Empty;
        if (!VirtualPath.IsValidName(name))
            return new FileEntryResult(FileOpStatus.InvalidName, Detail: $"Invalid name: '{name}'.");
        if (isFile && !IsPdfName(name))
            return new FileEntryResult(FileOpStatus.InvalidName, Detail: "A PDF file must keep its .pdf extension.");

        string oldName = Path.GetFileName(abs!);
        string parentVirtual = VirtualPath.GetParent(normalized);
        if (string.Equals(oldName, name, StringComparison.Ordinal))
            return new FileEntryResult(FileOpStatus.Ok, ToEntry(Info(abs!, isFile), parentVirtual)); // no-op

        string target = Path.Combine(Path.GetDirectoryName(abs!)!, name);
        if (FileSystemPathSafety.ContainsLink(target)) return new FileEntryResult(FileOpStatus.InvalidPath);
        // A case-only rename targets "itself" on a case-insensitive filesystem — allow it; everything
        // else colliding with an existing name is a conflict.
        bool caseOnly = string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            return new FileEntryResult(FileOpStatus.Conflict, Detail: $"'{name}' already exists.");

        if (isFile) File.Move(abs!, target);
        else Directory.Move(abs!, target);

        return new FileEntryResult(FileOpStatus.Ok, ToEntry(Info(target, isFile), parentVirtual));
    }

    public FileEntryResult Move(string? sourcePath, string? targetDirectory, bool overwrite)
    {
        var transfer = PrepareTransfer(sourcePath, targetDirectory, overwrite);
        if (transfer.Error is { } error) return error;
        if (!transfer.SourceIsFile && IsManagedDirectory(transfer.SourceAbs))
            return new FileEntryResult(FileOpStatus.ProtectedLocation);

        if (transfer.SourceIsFile)
        {
            File.Move(transfer.SourceAbs, transfer.TargetAbs, overwrite);
        }
        else
        {
            // Within the single managed root this never crosses volumes, so Directory.Move suffices;
            // an exotic junction-spanning setup surfaces as a clean IOException → 500 ProblemDetails.
            Directory.Move(transfer.SourceAbs, transfer.TargetAbs);
        }
        return new FileEntryResult(FileOpStatus.Ok, ToEntry(Info(transfer.TargetAbs, transfer.SourceIsFile), transfer.TargetDirNormalized));
    }

    public FileEntryResult Copy(string? sourcePath, string? targetDirectory, bool overwrite, CancellationToken ct = default)
    {
        var transfer = PrepareTransfer(sourcePath, targetDirectory, overwrite);
        if (transfer.Error is { } error) return error;

        if (transfer.SourceIsFile)
            File.Copy(transfer.SourceAbs, transfer.TargetAbs, overwrite);
        else
            CopyPdfTree(new DirectoryInfo(transfer.SourceAbs), transfer.TargetAbs, ct);

        return new FileEntryResult(FileOpStatus.Ok, ToEntry(Info(transfer.TargetAbs, transfer.SourceIsFile), transfer.TargetDirNormalized));
    }

    public FileSearchResult Search(string? path, string? query, bool recursive, CancellationToken ct = default)
    {
        var (status, abs, normalized) = Resolve(path);
        if (status is not FileOpStatus.Ok) return new FileSearchResult(status);
        if (File.Exists(abs)) return new FileSearchResult(FileOpStatus.NotAFolder);
        if (!Directory.Exists(abs)) return new FileSearchResult(FileOpStatus.NotFound);

        string q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
            return new FileSearchResult(FileOpStatus.InvalidName, Detail: "Search query is required.");

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true, // an unreadable subfolder must not kill the whole search
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var items = new List<FileSystemEntry>();
        bool truncated = false;
        foreach (var file in new DirectoryInfo(abs!).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPdfName(file.Name) || !file.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            if (items.Count >= _options.MaxSearchResults)
            {
                truncated = true;
                break;
            }
            string relative = Path.GetRelativePath(abs!, file.FullName).Replace('\\', '/');
            items.Add(new FileSystemEntry(
                file.Name, VirtualPath.Combine(normalized!, relative), false, file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
        }
        items.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        return new FileSearchResult(FileOpStatus.Ok, normalized!, q, items, truncated);
    }

    public FileDownloadResult ResolveDownload(string? path)
    {
        var (status, abs, _) = Resolve(path);
        if (status is not FileOpStatus.Ok) return new FileDownloadResult(status);
        if (Directory.Exists(abs)) return new FileDownloadResult(FileOpStatus.NotAFile);
        if (!File.Exists(abs)) return new FileDownloadResult(FileOpStatus.NotFound);
        return new FileDownloadResult(FileOpStatus.Ok, abs, Path.GetFileName(abs!));
    }

    public FileManagerStatus GetStatus()
    {
        string? resolvedFrom =
            _options.RootConfigurationKey ?? (
            !string.IsNullOrWhiteSpace(_options.RootPath) ? "FileManager:RootPath"
            : !string.IsNullOrWhiteSpace(_scopeSync.RootPath) ? "ScopeSync:RootPath"
            : null);

        var status = new FileManagerStatus
        {
            Configured = resolvedFrom is not null,
            ResolvedFrom = resolvedFrom,
            MaxUploadSizeMB = _options.MaxUploadSizeMB,
            ValidatePdfMagicBytes = _options.ValidatePdfMagicBytes,
            MaxSearchResults = _options.MaxSearchResults,
        };
        string? root;
        try { root = Root; }
        catch (Exception ex) when (ex is NetworkConfigurationException or IOException or UnauthorizedAccessException)
        {
            return status with { Error = $"Root folder is not reachable: {ex.Message}" };
        }
        if (root is null) return status;

        string rootFull;
        try
        {
            // Diagnostics must not turn a mistyped root into a new, apparently healthy empty store.
            rootFull = Path.GetFullPath(root);
            if (FileSystemPathSafety.ContainsLink(rootFull))
                return status with { RootPath = rootFull, Error = "Symbolic links and junctions are not supported in the storage path." };
            if (!Directory.Exists(rootFull))
                return status with { RootPath = rootFull, Error = "Root folder does not exist or is not accessible." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return status with { RootPath = root, Error = $"Root folder is not reachable: {ex.Message}" };
        }

        bool writable;
        string? error = null;
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(rootFull).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return status with { RootPath = rootFull, RootExists = true, Error = $"Root folder cannot be listed: {ex.Message}" };
        }
        string probePath = Path.Combine(rootFull, $"~probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);
            writable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writable = false;
            error = $"Root folder is not writable: {ex.Message}";
        }

        long? freeSpace = null, totalSpace = null;
        try
        {
            if (Path.GetPathRoot(rootFull) is { Length: > 0 } volume && !UncPath.IsUnc(volume))
            {
                var drive = new DriveInfo(volume);
                freeSpace = drive.AvailableFreeSpace;
                totalSpace = drive.TotalSize;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // free-space info is best-effort (UNC shares and exotic volumes don't expose it)
        }

        return status with
        {
            RootPath = rootFull,
            RootExists = true,
            Readable = true,
            Writable = writable,
            FreeSpaceBytes = freeSpace,
            TotalSpaceBytes = totalSpace,
            Error = error,
        };
    }

    // ---------------- plumbing ----------------

    private bool IsManagedDirectory(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(_scopeSync.RootPath)) return false;
        string scopeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ConnectRoot(_scopeSync.RootPath, _scopeSync.CredentialProfile)));
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
        string prefix = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        // Also protect an ancestor when the file manager exposes a broader tree than ScopeSync.
        if (string.Equals(path, scopeRoot, StringComparison.OrdinalIgnoreCase)
            || scopeRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        string relative = Path.GetRelativePath(scopeRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return false;
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Length <= 2 || parts.Length == 3
            && (parts[2].Equals("old", StringComparison.OrdinalIgnoreCase)
                || parts[2].Equals("new", StringComparison.OrdinalIgnoreCase)
                || parts[2].Equals("reports", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a virtual path against the existing root; configuration typos never create a new store.</summary>
    private (FileOpStatus Status, string? Absolute, string? Normalized) Resolve(string? path)
    {
        if (Root is not { } root) return (FileOpStatus.RootNotConfigured, null, null);
        if (!VirtualPath.TryResolve(root, path, out string? abs, out string? normalized))
            return (FileOpStatus.InvalidPath, null, null);
        if (FileSystemPathSafety.ContainsLink(abs)) return (FileOpStatus.InvalidPath, null, null);
        if (!Directory.Exists(root)) return (FileOpStatus.NotFound, null, null);
        return (FileOpStatus.Ok, abs, normalized);
    }

    private readonly record struct TransferPlan(
        FileEntryResult? Error, string SourceAbs = "", string TargetAbs = "", string TargetDirNormalized = "", bool SourceIsFile = false);

    /// <summary>
    /// Shared move/copy validation: resolves both sides, guards the root, a target inside the source
    /// (infinite recursion / self-destruction), source==target, and name conflicts (folder collisions
    /// are never overwritten; file overwrites only with <paramref name="overwrite"/>).
    /// </summary>
    private TransferPlan PrepareTransfer(string? sourcePath, string? targetDirectory, bool overwrite)
    {
        var (srcStatus, srcAbs, srcNorm) = Resolve(sourcePath);
        if (srcStatus is not FileOpStatus.Ok) return new TransferPlan(new FileEntryResult(srcStatus));
        if (srcNorm!.Length == 0) return new TransferPlan(new FileEntryResult(FileOpStatus.InvalidPath)); // the root itself

        bool isFile = File.Exists(srcAbs);
        if (!isFile && !Directory.Exists(srcAbs))
            return new TransferPlan(new FileEntryResult(FileOpStatus.NotFound));

        var (dstStatus, dstDirAbs, dstDirNorm) = Resolve(targetDirectory);
        if (dstStatus is not FileOpStatus.Ok) return new TransferPlan(new FileEntryResult(dstStatus));
        if (File.Exists(dstDirAbs)) return new TransferPlan(new FileEntryResult(FileOpStatus.NotAFolder));
        if (!Directory.Exists(dstDirAbs))
            return new TransferPlan(new FileEntryResult(FileOpStatus.NotFound, Detail: "Target folder not found."));

        if (!isFile && (string.Equals(dstDirNorm, srcNorm, StringComparison.OrdinalIgnoreCase)
                        || dstDirNorm!.StartsWith(srcNorm + "/", StringComparison.OrdinalIgnoreCase)))
            return new TransferPlan(new FileEntryResult(FileOpStatus.InvalidPath, Detail: "Target folder is inside the source folder."));

        string name = Path.GetFileName(srcAbs!);
        string targetAbs = Path.Combine(dstDirAbs!, name);
        if (FileSystemPathSafety.ContainsLink(targetAbs))
            return new TransferPlan(new FileEntryResult(FileOpStatus.InvalidPath));
        if (string.Equals(VirtualPath.Combine(dstDirNorm!, name), srcNorm, StringComparison.OrdinalIgnoreCase))
            return new TransferPlan(new FileEntryResult(FileOpStatus.Conflict, Detail: "Source and target are the same."));

        if (Directory.Exists(targetAbs) || (File.Exists(targetAbs) && (!isFile || !overwrite)))
            return new TransferPlan(new FileEntryResult(
                FileOpStatus.Conflict,
                Detail: !isFile || Directory.Exists(targetAbs)
                    ? $"'{name}' already exists in the target folder (folders are never merged or overwritten)."
                    : $"'{name}' already exists in the target folder."));

        return new TransferPlan(null, srcAbs!, targetAbs, dstDirNorm!, isFile);
    }

    /// <summary>Recursive folder copy of what the manager shows: subfolders + PDFs (hidden/system skipped).</summary>
    private static void CopyPdfTree(DirectoryInfo source, string targetAbs, CancellationToken ct)
    {
        if (FileSystemPathSafety.ContainsLink(source.FullName) || FileSystemPathSafety.ContainsLink(targetAbs))
            throw new IOException("Symbolic links and junctions are not supported.");
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

    private static FileSystemEntry ToEntry(FileSystemInfo info, string parentVirtualPath) => new(
        info.Name,
        VirtualPath.Combine(parentVirtualPath, info.Name),
        info is DirectoryInfo,
        (info as FileInfo)?.Length,
        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

    private static FileSystemInfo Info(string absolutePath, bool isFile) =>
        isFile ? new FileInfo(absolutePath) : new DirectoryInfo(absolutePath);

    private static bool IsHiddenOrSystem(FileSystemInfo info) =>
        (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0;

    private static bool IsPdfName(string name) =>
        name.EndsWith(PdfExtension, StringComparison.OrdinalIgnoreCase)
        && name.Length > PdfExtension.Length;

    private static FileUploadResult Soft(string name, string code, string message) =>
        new(name, ErrorCode: code, ErrorMessage: message);

    private static async Task<FileUploadResult> AbortAsync(FileStream temp, string tempPath, string name, string code, string message)
    {
        await temp.DisposeAsync();
        TryDelete(tempPath);
        return Soft(name, code, message);
    }

    private static FileUploadResult Cleanup(string tempPath, FileUploadResult result)
    {
        TryDelete(tempPath);
        return result;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
