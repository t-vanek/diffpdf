using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Computes the <see cref="EffectiveConfiguration"/> for a scope by applying the inheritance rules across
/// Global → Branch → Instance. This is the single place the inheritance logic lives — callers (API, the
/// trigger run path, the UI via the API) consume the resolved result and never re-implement the rules.
/// </summary>
public interface IScopeConfigurationResolver
{
    /// <summary>Resolve the effective config for an instance (walks Instance → Branch → Global).</summary>
    Task<EffectiveConfiguration> ResolveForInstanceAsync(Guid branchId, Guid instanceId, CancellationToken ct = default);

    /// <summary>Resolve the effective config for a branch (walks Branch → Global).</summary>
    Task<EffectiveConfiguration> ResolveForBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>The global effective config (always the global payload).</summary>
    Task<EffectiveConfiguration> ResolveGlobalAsync(CancellationToken ct = default);
}
