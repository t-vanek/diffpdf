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
public sealed record StructureItem(string Name, string Path, StructureItemState State, string? Detail = null);

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
    /// <summary>Read-only: reports whether each required subfolder is present / missing / wrong-typed.</summary>
    Task<InstanceStructureReport> InspectAsync(string basePath, string? credentialProfile, CancellationToken ct = default);

    /// <summary>Creates any missing subfolder and replaces a file occupying a subfolder name (destructive).</summary>
    Task<InstanceStructureReport> EnsureAsync(string basePath, string? credentialProfile, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class InstanceStructureService(
    INetworkShareResolver resolver,
    INetworkShareConnector shareConnector,
    ILogger<InstanceStructureService> logger) : IInstanceStructureService
{
    /// <summary>The fixed subfolders every instance base must contain.</summary>
    public static readonly IReadOnlyList<string> RequiredSubfolders = ["old", "new", "reports"];

    public Task<InstanceStructureReport> InspectAsync(string basePath, string? credentialProfile, CancellationToken ct = default) =>
        RunAsync(basePath, credentialProfile, ensure: false, ct);

    public Task<InstanceStructureReport> EnsureAsync(string basePath, string? credentialProfile, CancellationToken ct = default) =>
        RunAsync(basePath, credentialProfile, ensure: true, ct);

    private Task<InstanceStructureReport> RunAsync(string basePath, string? credentialProfile, bool ensure, CancellationToken ct) =>
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
                    items.Add(ensure ? Ensure(path, name) : Inspect(path, name));
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
