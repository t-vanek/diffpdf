namespace DiffPdf.Api.Auth;

/// <summary>Fluent helpers to require the write / admin policy on an endpoint (read is the fallback).</summary>
public static class AuthorizationExtensions
{
    /// <summary>Requires the <see cref="RolePolicies.Write"/> policy (≥ Operator). Permissive when auth is off.</summary>
    public static TBuilder RequireWrite<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(RolePolicies.Write);

    /// <summary>Requires the <see cref="RolePolicies.Admin"/> policy. Permissive when auth is off.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(RolePolicies.Admin);
}
