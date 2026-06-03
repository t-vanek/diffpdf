using DiffPdf.Core.Network;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Discovery mode: surface the centrally configured network shares and credential
/// profiles, so testers can see which aliases a batch or instance may reference.
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
    }
}
