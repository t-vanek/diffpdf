using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Core.Network;

/// <summary>State of one required subfolder of an instance's base path.</summary>
public enum StructureItemState
{
    /// <summary>Directory exists (OK).</summary>
    Present,
    /// <summary>Nothing exists at this name (inspect only).</summary>
    Missing,
    /// <summary>A file occupies the name where a directory is required (inspect only).</summary>
    WrongType,
    /// <summary>Was missing, now created (ensure only).</summary>
    Created,
    /// <summary>A colliding file was deleted and the directory created (ensure only).</summary>
    Repaired,
}

/// <summary>State of one required subfolder after inspecting or ensuring it.</summary>
public sealed record StructureItem(string Name, string Path, StructureItemState State, string? Detail = null)
{
    /// <summary>Number of *.pdf files (recursive) in the folder; null for reports or a folder that does not exist. 0 = empty.</summary>
    public int? PdfCount { get; init; }

    /// <summary>Complete list of *.pdf relative paths; populated only when the caller requested files.</summary>
    public IReadOnlyList<string>? Files { get; init; }
}

/// <summary>What inspecting / ensuring an instance's old/new/reports skeleton found or did.</summary>
public sealed record InstanceStructureReport(
    bool Reachable,
    string BasePath,
    IReadOnlyList<StructureItem> Items,
    string? Error)
{
    /// <summary>True when the base was reachable and no item is still missing or wrong-typed.</summary>
    public bool Ok => Reachable && Error is null
        && Items.All(i => i.State is not (StructureItemState.Missing or StructureItemState.WrongType));

    /// <summary>Number of *.pdf files in the <c>old</c> input folder (0 when missing/empty).</summary>
    public int OldPdfCount => Items.FirstOrDefault(i => i.Name == "old")?.PdfCount ?? 0;

    /// <summary>Number of *.pdf files in the <c>new</c> input folder (0 when missing/empty).</summary>
    public int NewPdfCount => Items.FirstOrDefault(i => i.Name == "new")?.PdfCount ?? 0;

    /// <summary>
    /// The "OK to submit a batch" signal: base reachable and both input folders hold at
    /// least one PDF. Shared by the readiness view and the job-start pre-flight.
    /// </summary>
    public bool HasComparableInputs => Reachable && OldPdfCount > 0 && NewPdfCount > 0;
}

/// <summary>
/// Inspects and provisions the conventional <c>old/</c>, <c>new/</c> and
/// <c>reports/</c> skeleton under an instance's base path. Works for local and
/// authenticated network bases via <see cref="INetworkShareConnector"/>.
/// </summary>
public interface IInstanceStructureService
{
    /// <summary>
    /// Read-only: reports whether each required subfolder is present / missing / wrong-typed, and how
    /// many PDFs the old/new input folders hold. Pass <paramref name="includeFiles"/> to also return the
    /// complete file list for old/new.
    /// </summary>
    Task<InstanceStructureReport> InspectAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default);

    /// <summary>Creates any missing subfolder and replaces a file occupying a subfolder name (destructive).</summary>
    Task<InstanceStructureReport> EnsureAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default);

    /// <summary>
    /// Creates a single folder (resolving a share alias / UNC / credential profile), <b>without</b> the
    /// old/new/reports skeleton — used to provision a branch folder under the structure root. Best-effort:
    /// returns false if the path could not be reached/created.
    /// </summary>
    Task<bool> EnsureFolderAsync(string path, string? credentialProfile, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class InstanceStructureService(
    INetworkShareResolver resolver,
    INetworkShareConnector shareConnector,
    ILogger<InstanceStructureService> logger) : IInstanceStructureService
{
    /// <summary>The fixed subfolders every instance base must contain.</summary>
    public static readonly IReadOnlyList<string> RequiredSubfolders = ["old", "new", "reports"];

    public Task<InstanceStructureReport> InspectAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default) =>
        RunAsync(basePath, credentialProfile, ensure: false, includeFiles, ct);

    public Task<InstanceStructureReport> EnsureAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default) =>
        RunAsync(basePath, credentialProfile, ensure: true, includeFiles, ct);

    public Task<bool> EnsureFolderAsync(string path, string? credentialProfile, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                var resolved = resolver.Resolve(path, inlineCredentials: null, credentialProfile);
                using var connection = shareConnector.Connect(resolved.Path, resolved.Credentials);
                Directory.CreateDirectory(connection.Path);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ensure folder failed for {Path}", path);
                return false;
            }
        }, ct);

    private Task<InstanceStructureReport> RunAsync(string basePath, string? credentialProfile, bool ensure, bool includeFiles, CancellationToken ct) =>
        Task.Run(() =>
        {
            ResolvedFolder resolved;
            try
            {
                resolved = resolver.Resolve(basePath, inlineCredentials: null, credentialProfile);
            }
            catch (NetworkConfigurationException ex)
            {
                return new InstanceStructureReport(false, basePath, [], ex.Message);
            }

            try
            {
                using var connection = shareConnector.Connect(resolved.Path, resolved.Credentials);

                // Create the base itself if missing (parents included for local paths).
                if (ensure)
                    Directory.CreateDirectory(connection.Path);

                var items = new List<StructureItem>(RequiredSubfolders.Count);
                foreach (var name in RequiredSubfolders)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = Path.Combine(connection.Path, name);
                    var item = ensure ? Ensure(path, name) : Inspect(path, name);

                    // Report PDF content of the input folders (old/new) when they exist.
                    if (name is "old" or "new" &&
                        item.State is StructureItemState.Present or StructureItemState.Created or StructureItemState.Repaired)
                    {
                        var (count, files) = PdfFolderScanner.Scan(path, "*.pdf", recursive: true, sampleLimit: includeFiles ? null : 0, ct);
                        item = item with { PdfCount = count, Files = includeFiles ? files : null };
                    }

                    items.Add(item);
                }

                return new InstanceStructureReport(true, resolved.Path, items, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Instance structure {Op} failed for {Base}", ensure ? "ensure" : "inspect", resolved.Path);
                return new InstanceStructureReport(false, resolved.Path, [], ex.Message);
            }
        }, ct);

    private static StructureItem Inspect(string path, string name)
    {
        if (Directory.Exists(path)) return new StructureItem(name, path, StructureItemState.Present);
        if (File.Exists(path)) return new StructureItem(name, path, StructureItemState.WrongType, "A file occupies this name.");
        return new StructureItem(name, path, StructureItemState.Missing);
    }

    private StructureItem Ensure(string path, string name)
    {
        if (Directory.Exists(path))
            return new StructureItem(name, path, StructureItemState.Present);

        if (File.Exists(path))
        {
            // Destructive repair (explicitly opted in): a file occupies the folder name.
            logger.LogWarning("Replacing file {Path} with directory '{Name}' (wrong-type repair)", path, name);
            File.Delete(path);
            Directory.CreateDirectory(path);
            return new StructureItem(name, path, StructureItemState.Repaired, "Replaced a colliding file.");
        }

        Directory.CreateDirectory(path);
        return new StructureItem(name, path, StructureItemState.Created);
    }
}
