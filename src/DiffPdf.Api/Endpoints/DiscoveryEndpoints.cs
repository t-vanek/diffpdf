using DiffPdf.Core.Network;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Discovery mode: inspect what is reachable and what would be compared, without
/// running a job. Lets testers validate paths, credentials and pairing up front.
/// </summary>
public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/discovery").WithTags("Discovery");

        group.MapGet("/shares", (INetworkShareResolver resolver) =>
            Results.Ok(new NetworkConfigSummary(resolver.ListShares(), resolver.ListCredentialProfiles())))
            .WithSummary("List configured network shares and credential-profile names")
            .Produces<NetworkConfigSummary>();

        group.MapPost("/folder", async (
            DiscoverFolderRequest request, INetworkDiscoveryService discovery, CancellationToken ct) =>
        {
            var result = await discovery.DiscoverFolderAsync(
                request.Folder, request.Credentials, request.CredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);
            return Results.Ok(result);
        })
        .WithSummary("Probe a folder for reachability and PDF count")
        .Produces<FolderDiscovery>();

        group.MapPost("/preview", async (
            PreviewPairingRequest request, INetworkDiscoveryService discovery, CancellationToken ct) =>
        {
            var result = await discovery.PreviewPairingAsync(
                request.OldFolder, request.NewFolder,
                request.OldFolderCredentials, request.OldFolderCredentialProfile,
                request.NewFolderCredentials, request.NewFolderCredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);
            return Results.Ok(result);
        })
        .WithSummary("Dry-run an old/new folder pairing without comparing")
        .Produces<PairingPreview>();
    }
}
