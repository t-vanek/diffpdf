using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Core.Network;

/// <summary>What a single folder probe found.</summary>
public sealed record FolderDiscovery(
    bool Reachable,
    string ResolvedPath,
    string? ShareName,
    int FileCount,
    IReadOnlyList<string> SampleFiles,
    string? Error);

/// <summary>
/// A dry-run of a batch: how the old and new folders pair up, without comparing
/// any PDFs. Lets a tester validate paths, credentials and pairing before
/// submitting the (expensive) job.
/// </summary>
public sealed record PairingPreview(
    bool Reachable,
    string OldResolvedPath,
    string NewResolvedPath,
    int Total,
    int Matched,
    int OnlyInOld,
    int OnlyInNew,
    IReadOnlyList<string> SampleOnlyInOld,
    IReadOnlyList<string> SampleOnlyInNew,
    string? Error);

/// <summary>
/// "Discovery mode": connects to configured / ad-hoc folders and reports what is
/// reachable and what would be compared — without running a comparison.
/// </summary>
public interface INetworkDiscoveryService
{
    Task<FolderDiscovery> DiscoverFolderAsync(
        string folder,
        NetworkCredentials? inlineCredentials = null,
        string? credentialProfile = null,
        string searchPattern = "*.pdf",
        bool recursive = true,
        int sampleSize = 20,
        CancellationToken ct = default);

    Task<PairingPreview> PreviewPairingAsync(
        string oldFolder,
        string newFolder,
        NetworkCredentials? oldInlineCredentials = null,
        string? oldCredentialProfile = null,
        NetworkCredentials? newInlineCredentials = null,
        string? newCredentialProfile = null,
        string searchPattern = "*.pdf",
        bool recursive = true,
        int sampleSize = 20,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class NetworkDiscoveryService(
    INetworkShareResolver resolver,
    INetworkShareConnector shareConnector,
    ILogger<NetworkDiscoveryService> logger) : INetworkDiscoveryService
{
    public Task<FolderDiscovery> DiscoverFolderAsync(
        string folder,
        NetworkCredentials? inlineCredentials = null,
        string? credentialProfile = null,
        string searchPattern = "*.pdf",
        bool recursive = true,
        int sampleSize = 20,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ResolvedFolder resolved;
            try
            {
                resolved = resolver.Resolve(folder, inlineCredentials, credentialProfile);
            }
            catch (NetworkConfigurationException ex)
            {
                return new FolderDiscovery(false, folder, null, 0, [], ex.Message);
            }

            try
            {
                using var connection = shareConnector.Connect(resolved.Path, resolved.Credentials);
                var files = Enumerate(connection.Path, searchPattern, recursive, sampleSize, out int count, ct);
                return new FolderDiscovery(true, resolved.Path, resolved.ShareName, count, files, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Folder discovery failed for {Folder}", resolved.Path);
                return new FolderDiscovery(false, resolved.Path, resolved.ShareName, 0, [], ex.Message);
            }
        }, ct);

    public Task<PairingPreview> PreviewPairingAsync(
        string oldFolder,
        string newFolder,
        NetworkCredentials? oldInlineCredentials = null,
        string? oldCredentialProfile = null,
        NetworkCredentials? newInlineCredentials = null,
        string? newCredentialProfile = null,
        string searchPattern = "*.pdf",
        bool recursive = true,
        int sampleSize = 20,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ResolvedFolder oldResolved, newResolved;
            try
            {
                oldResolved = resolver.Resolve(oldFolder, oldInlineCredentials, oldCredentialProfile);
                newResolved = resolver.Resolve(newFolder, newInlineCredentials, newCredentialProfile);
            }
            catch (NetworkConfigurationException ex)
            {
                return Empty(oldFolder, newFolder, ex.Message);
            }

            try
            {
                using var oldShare = shareConnector.Connect(oldResolved.Path, oldResolved.Credentials);
                using var newShare = shareConnector.Connect(newResolved.Path, newResolved.Credentials);

                var pairs = FolderPairing.Pair(oldShare.Path, newShare.Path, searchPattern, recursive);

                int matched = pairs.Count(p => p.OldPath is not null && p.NewPath is not null);
                var onlyInOld = pairs.Where(p => p.NewPath is null).Select(p => p.RelativePath).ToList();
                var onlyInNew = pairs.Where(p => p.OldPath is null).Select(p => p.RelativePath).ToList();

                return new PairingPreview(
                    Reachable: true,
                    OldResolvedPath: oldResolved.Path,
                    NewResolvedPath: newResolved.Path,
                    Total: pairs.Count,
                    Matched: matched,
                    OnlyInOld: onlyInOld.Count,
                    OnlyInNew: onlyInNew.Count,
                    SampleOnlyInOld: onlyInOld.Take(sampleSize).ToList(),
                    SampleOnlyInNew: onlyInNew.Take(sampleSize).ToList(),
                    Error: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pairing preview failed for {Old} vs {New}", oldResolved.Path, newResolved.Path);
                return Empty(oldResolved.Path, newResolved.Path, ex.Message);
            }
        }, ct);

    private static IReadOnlyList<string> Enumerate(
        string root, string pattern, bool recursive, int sampleSize, out int count, CancellationToken ct)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Folder not found: {root}");

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var sample = new List<string>(Math.Max(0, sampleSize));
        count = 0;

        foreach (var file in Directory.EnumerateFiles(root, pattern, option))
        {
            ct.ThrowIfCancellationRequested();
            count++;
            if (sample.Count < sampleSize)
                sample.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        return sample;
    }

    private static PairingPreview Empty(string oldPath, string newPath, string error) =>
        new(false, oldPath, newPath, 0, 0, 0, 0, [], [], error);
}
