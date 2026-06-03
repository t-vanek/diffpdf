using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Core.Network;

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
/// "Discovery mode": connects to configured folders and reports how they would pair
/// up — without running a comparison. Backs the instance readiness pre-flight.
/// </summary>
public interface INetworkDiscoveryService
{
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

    private static PairingPreview Empty(string oldPath, string newPath, string error) =>
        new(false, oldPath, newPath, 0, 0, 0, 0, [], [], error);
}
