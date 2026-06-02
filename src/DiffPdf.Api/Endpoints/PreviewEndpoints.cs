using DiffPdf.Core.Network;
using DiffPdf.Core.Preview;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Batch dry-run: inspect what is reachable and what would be compared, without
/// running a job. (Server <em>discovery</em> — finding the server itself — lives
/// in <see cref="ServerInfoEndpoints"/> and the UDP responder.)
/// </summary>
public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/preview").WithTags("Preview");

        group.MapGet("/shares", (INetworkShareResolver resolver) =>
            Results.Ok(new NetworkConfigSummary(resolver.ListShares(), resolver.ListCredentialProfiles())))
            .WithSummary("List configured network shares and credential-profile names")
            .Produces<NetworkConfigSummary>();

        group.MapPost("/folder", async (
            InspectFolderRequest request, IBatchPreviewService preview, CancellationToken ct) =>
        {
            var result = await preview.InspectFolderAsync(
                request.Folder, request.Credentials, request.CredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);
            return Results.Ok(result);
        })
        .WithSummary("Probe a folder for reachability and PDF count")
        .Produces<FolderInspection>();

        group.MapPost("/pairing", async (
            PreviewPairingRequest request, IBatchPreviewService preview, CancellationToken ct) =>
        {
            var result = await preview.PreviewPairingAsync(
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
