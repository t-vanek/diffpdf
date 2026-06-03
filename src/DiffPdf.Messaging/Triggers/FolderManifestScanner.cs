using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Network;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Triggers;

/// <summary>Computes a <see cref="FolderManifest"/> for an instance's <c>new/</c> input folder.</summary>
public interface IFolderManifestScanner
{
    /// <summary>Returns the manifest of the instance's <c>new/</c> folder, or null when the base is unreachable.</summary>
    FolderManifest? ScanNewFolder(string basePath, string? credentialProfile, CancellationToken ct);
}

/// <inheritdoc />
public sealed class FolderManifestScanner(
    INetworkShareResolver resolver,
    INetworkShareConnector connector,
    ILogger<FolderManifestScanner> logger) : IFolderManifestScanner
{
    public FolderManifest? ScanNewFolder(string basePath, string? credentialProfile, CancellationToken ct)
    {
        ResolvedFolder resolved;
        try
        {
            resolved = resolver.Resolve(basePath, inlineCredentials: null, credentialProfile);
        }
        catch (NetworkConfigurationException ex)
        {
            logger.LogWarning(ex, "Folder-watch: cannot resolve base '{Base}'.", basePath);
            return null;
        }

        try
        {
            using var connection = connector.Connect(resolved.Path, resolved.Credentials);
            string newFolder = InstanceFolders.New(connection.Path);
            if (!Directory.Exists(newFolder))
                return FolderManifest.Empty;

            int count = 0;
            long totalSize = 0;
            long latestTicks = 0;
            foreach (var file in Directory.EnumerateFiles(newFolder, "*.pdf", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                count++;
                totalSize += info.Length;
                long ticks = info.LastWriteTimeUtc.Ticks;
                if (ticks > latestTicks)
                    latestTicks = ticks;
            }

            return new FolderManifest(count, totalSize, latestTicks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Folder-watch: scan failed for base '{Base}'.", resolved.Path);
            return null;
        }
    }
}
