using DiffPdf.Core.Abstractions;
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
                        var (count, files) = CountPdfs(path, includeFiles, ct);
                        item = item with { PdfCount = count, Files = files };
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

    /// <summary>Counts *.pdf files (recursive) under a folder; collects their relative paths only when requested.</summary>
    private static (int Count, IReadOnlyList<string>? Files) CountPdfs(string folder, bool includeFiles, CancellationToken ct)
    {
        int count = 0;
        List<string>? files = includeFiles ? [] : null;

        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            count++;
            files?.Add(Path.GetRelativePath(folder, file).Replace('\\', '/'));
        }

        return (count, files);
    }
}
