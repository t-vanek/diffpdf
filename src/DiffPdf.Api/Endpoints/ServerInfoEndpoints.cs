using DiffPdf.Api.Discovery;
using DiffPdf.Core.Discovery;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Exposes the server's self-description so a client that already has the base URL
/// (from UDP discovery or configuration) can learn its capabilities and how to
/// authenticate. Always anonymous.
/// </summary>
public static class ServerInfoEndpoints
{
    public static void MapServerInfoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/server-info", (HttpContext context, IServerDescriptorProvider provider) =>
        {
            string baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            return Results.Ok(provider.Create(baseUrl));
        })
        .AllowAnonymous()
        .WithTags("Discovery")
        .WithSummary("Server self-description (capabilities, auth, hub URL)")
        .Produces<DiffPdfServerDescriptor>();
    }
}
