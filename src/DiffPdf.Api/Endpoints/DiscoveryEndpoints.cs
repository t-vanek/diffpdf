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
            IBranchStore branches,
            IInstanceStore instances,
            CancellationToken ct) =>
        {
            var (valid, scope) = await ResolveScopeAsync(
                request.BranchKey, request.InstanceKey, branches, instances, ct);
            if (!valid)
                return MismatchedScopeProblem();

            var folder = await discovery.DiscoverFolderAsync(
                request.Folder, request.Credentials, request.CredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);

            return Results.Ok(new FolderDiscoveryResult(scope, folder));
        })
        .WithSummary("Probe a folder for reachability + PDF count, optionally validating a branch/instance scope")
        .Produces<FolderDiscoveryResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/preview", async (
            PreviewPairingRequest request,
            INetworkDiscoveryService discovery,
            IBranchStore branches,
            IInstanceStore instances,
            CancellationToken ct) =>
        {
            var (valid, scope) = await ResolveScopeAsync(
                request.BranchKey, request.InstanceKey, branches, instances, ct);
            if (!valid)
                return MismatchedScopeProblem();

            var pairing = await discovery.PreviewPairingAsync(
                request.OldFolder, request.NewFolder,
                request.OldFolderCredentials, request.OldFolderCredentialProfile,
                request.NewFolderCredentials, request.NewFolderCredentialProfile,
                request.SearchPattern, request.Recursive, request.SampleSize, ct);

            return Results.Ok(new PairingPreviewResult(scope, pairing));
        })
        .WithSummary("Dry-run an old/new folder pairing, optionally validating a branch/instance scope")
        .Produces<PairingPreviewResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Validates an optional branch/instance scope. Returns <c>valid=false</c> when
    /// exactly one of the two keys is supplied; otherwise a <see cref="ScopeCheck"/>
    /// (or null when neither key was supplied).
    /// </summary>
    private static async Task<(bool Valid, ScopeCheck? Scope)> ResolveScopeAsync(
        string? branchKey, string? instanceKey,
        IBranchStore branches, IInstanceStore instances, CancellationToken ct)
    {
        bool hasBranch = !string.IsNullOrWhiteSpace(branchKey);
        bool hasInstance = !string.IsNullOrWhiteSpace(instanceKey);
        if (hasBranch != hasInstance)
            return (false, null);
        if (!hasBranch)
            return (true, null);

        var branch = await branches.GetByKeyAsync(branchKey!, ct);
        var instance = branch is null
            ? null
            : await instances.GetByKeyAsync(branch.Id, instanceKey!, ct);

        return (true, new ScopeCheck(
            branchKey!, branch is not null, branch?.Name,
            instanceKey!, instance is not null, instance?.Name));
    }

    private static IResult MismatchedScopeProblem() => Results.Problem(
        "Provide both branchKey and instanceKey, or neither.",
        statusCode: StatusCodes.Status400BadRequest);
}
