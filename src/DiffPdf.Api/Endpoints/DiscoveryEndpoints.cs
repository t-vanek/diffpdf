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
            var (valid, scope) = await ResolveScopeAsync(
                request.BusinessInstanceKey, request.ProjectKey, instances, projects, ct);
            if (!valid)
                return MismatchedScopeProblem();

            var folder = await discovery.DiscoverFolderAsync(
                request.Folder, request.Credentials, request.CredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);

            return Results.Ok(new FolderDiscoveryResult(scope, folder));
        })
        .WithSummary("Probe a folder for reachability + PDF count, optionally validating a business-instance/project scope")
        .Produces<FolderDiscoveryResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/preview", async (
            PreviewPairingRequest request,
            INetworkDiscoveryService discovery,
            IBusinessInstanceStore instances,
            IProjectStore projects,
            CancellationToken ct) =>
        {
            var (valid, scope) = await ResolveScopeAsync(
                request.BusinessInstanceKey, request.ProjectKey, instances, projects, ct);
            if (!valid)
                return MismatchedScopeProblem();

            var pairing = await discovery.PreviewPairingAsync(
                request.OldFolder, request.NewFolder,
                request.OldFolderCredentials, request.OldFolderCredentialProfile,
                request.NewFolderCredentials, request.NewFolderCredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);

            return Results.Ok(new PairingPreviewResult(scope, pairing));
        })
        .WithSummary("Dry-run an old/new folder pairing, optionally validating a business-instance/project scope")
        .Produces<PairingPreviewResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Validates an optional business-instance/project scope. Returns
    /// <c>valid=false</c> when exactly one of the two keys is supplied; otherwise a
    /// <see cref="ScopeCheck"/> (or null when neither key was supplied).
    /// </summary>
    private static async Task<(bool Valid, ScopeCheck? Scope)> ResolveScopeAsync(
        string? businessInstanceKey, string? projectKey,
        IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct)
    {
        bool hasInstance = !string.IsNullOrWhiteSpace(businessInstanceKey);
        bool hasProject = !string.IsNullOrWhiteSpace(projectKey);
        if (hasInstance != hasProject)
            return (false, null);
        if (!hasInstance)
            return (true, null);

        var instance = await instances.GetByKeyAsync(businessInstanceKey!, ct);
        var project = instance is null
            ? null
            : await projects.GetByKeyAsync(instance.Id, projectKey!, ct);

        return (true, new ScopeCheck(
            businessInstanceKey!, instance is not null, instance?.Name,
            projectKey!, project is not null, project?.Name));
    }

    private static IResult MismatchedScopeProblem() => Results.Problem(
        "Provide both businessInstanceKey and projectKey, or neither.",
        statusCode: StatusCodes.Status400BadRequest);
}
