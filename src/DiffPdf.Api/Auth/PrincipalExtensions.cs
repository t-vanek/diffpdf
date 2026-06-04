using System.Security.Claims;
using DiffPdf.Core.Models;

namespace DiffPdf.Api.Auth;

/// <summary>Helpers to derive the acting identity and role from a request principal (token or API key).</summary>
public static class PrincipalExtensions
{
    /// <summary>The acting principal for audit (client id / key name / subject), or null when unauthenticated.</summary>
    public static string? Actor(this ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
            ? user.FindFirst("client_id")?.Value
              ?? user.FindFirst("name")?.Value
              ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? user.FindFirst("sub")?.Value
              ?? user.Identity.Name
            : null;

    /// <summary>The actor, or <c>"system"</c> when unauthenticated (auth disabled).</summary>
    public static string ActorOrSystem(this ClaimsPrincipal? user) => user.Actor() ?? "system";

    /// <summary>The highest role the principal holds (from its role claims); <see cref="Role.Viewer"/> by default.</summary>
    public static Role HighestRole(this ClaimsPrincipal user) =>
        user.HasClaim(RoleClaims.ClaimType, nameof(Role.Admin)) ? Role.Admin
        : user.HasClaim(RoleClaims.ClaimType, nameof(Role.Operator)) ? Role.Operator
        : Role.Viewer;

    /// <summary>How the principal authenticated: <c>apikey</c> or <c>token</c>.</summary>
    public static string AuthMethod(this ClaimsPrincipal user) =>
        user.FindFirst("auth_method")?.Value ?? "token";
}
