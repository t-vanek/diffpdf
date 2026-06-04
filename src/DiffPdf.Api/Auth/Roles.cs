using System.Security.Claims;
using DiffPdf.Core.Models;

namespace DiffPdf.Api.Auth;

/// <summary>Names of the authorization policies enforced across the API.</summary>
public static class RolePolicies
{
    /// <summary>Read access — requires at least <see cref="Role.Viewer"/>.</summary>
    public const string Read = "read";
    /// <summary>Write access (create/update/delete, run) — requires at least <see cref="Role.Operator"/>.</summary>
    public const string Write = "write";
    /// <summary>Security administration — requires <see cref="Role.Admin"/>.</summary>
    public const string Admin = "admin";
}

/// <summary>Maps a <see cref="Role"/> to the (hierarchical) role claims a principal carries.</summary>
public static class RoleClaims
{
    /// <summary>The claim type used for roles (matches OpenIddict's <c>role</c> claim).</summary>
    public const string ClaimType = "role";

    /// <summary>The role + all roles it implies (Admin ⇒ Operator ⇒ Viewer), as claim values.</summary>
    public static IReadOnlyList<string> Expand(Role role) => role switch
    {
        Role.Admin => [nameof(Role.Admin), nameof(Role.Operator), nameof(Role.Viewer)],
        Role.Operator => [nameof(Role.Operator), nameof(Role.Viewer)],
        _ => [nameof(Role.Viewer)],
    };

    /// <summary>Parses a role name (case-insensitive); falls back to <see cref="Role.Viewer"/>.</summary>
    public static Role Parse(string? value) =>
        Enum.TryParse<Role>(value, ignoreCase: true, out var r) ? r : Role.Viewer;

    /// <summary>Role claims for an identity (hierarchical), e.g. for building an API-key principal.</summary>
    public static IEnumerable<Claim> ClaimsFor(Role role) =>
        Expand(role).Select(r => new Claim(ClaimType, r));
}
