using DiffPdf.Core.Network;
using DiffPdf.Persistence;

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
            DiscoverFolderRequest request,
            INetworkDiscoveryService discovery,
            IBusinessInstanceStore instances,
            IProjectStore projects,
            CancellationToken ct) =>
        {
            bool hasInstance = !string.IsNullOrWhiteSpace(request.BusinessInstanceKey);
            bool hasProject = !string.IsNullOrWhiteSpace(request.ProjectKey);
            if (hasInstance != hasProject)
                return Results.Problem(
                    "Provide both businessInstanceKey and projectKey, or neither.",
                    statusCode: StatusCodes.Status400BadRequest);

            ScopeCheck? scope = null;
            if (hasInstance)
            {
                var instance = await instances.GetByKeyAsync(request.BusinessInstanceKey!, ct);
                var project = instance is null
                    ? null
                    : await projects.GetByKeyAsync(instance.Id, request.ProjectKey!, ct);
                scope = new ScopeCheck(
                    request.BusinessInstanceKey!, instance is not null, instance?.Name,
                    request.ProjectKey!, project is not null, project?.Name);
            }

            var folder = await discovery.DiscoverFolderAsync(
                request.Folder, request.Credentials, request.CredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);

            return Results.Ok(new FolderDiscoveryResult(scope, folder));
        })
        .WithSummary("Probe a folder for reachability + PDF count, optionally validating a business-instance/project scope")
        .Produces<FolderDiscoveryResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

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
