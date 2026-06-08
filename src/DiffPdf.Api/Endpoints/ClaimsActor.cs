using System.Security.Claims;

namespace DiffPdf.Api.Endpoints;

/// <summary>Resolves the acting principal for audit logging from the request's claims.</summary>
internal static class ClaimsActor
{
    /// <summary>The acting principal (token client id / subject / name), or null when unauthenticated (dev mode).</summary>
    public static string? Actor(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirst("client_id")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name
            : null;
}
