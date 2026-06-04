using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for per-scope configuration (triggers + comparers) used by the inheritance resolver.
/// Exactly one row exists per scope target: one global row, one per branch, one per instance.
/// Updates use optimistic concurrency (expectedVersion) and surface
/// <see cref="DiffPdf.Core.Storage.DuplicateKeyException"/> /
/// <see cref="DiffPdf.Core.Storage.ConcurrencyConflictException"/>.
/// </summary>
public interface IScopeConfigurationStore
{
    /// <summary>
    /// Creates a configuration row; throws DuplicateKeyException when a row for the same scope target
    /// already exists (a second global row, or a second row for the same branch/instance).
    /// </summary>
    Task<ScopeConfiguration> CreateAsync(ScopeConfiguration config, CancellationToken ct = default);

    /// <summary>The single global configuration row, or null if not yet provisioned.</summary>
    Task<ScopeConfiguration?> GetGlobalAsync(CancellationToken ct = default);

    /// <summary>A branch's configuration row, or null if not yet provisioned.</summary>
    Task<ScopeConfiguration?> GetByBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>An instance's configuration row, or null if not yet provisioned.</summary>
    Task<ScopeConfiguration?> GetByInstanceAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>
    /// Updates a row (matched by <see cref="ScopeConfiguration.Id"/>); throws ConcurrencyConflictException
    /// on version mismatch.
    /// </summary>
    Task<ScopeConfiguration> UpdateAsync(ScopeConfiguration config, long expectedVersion, CancellationToken ct = default);

    /// <summary>Removes a branch's configuration row (when the branch is deleted). Returns false if absent.</summary>
    Task<bool> DeleteByBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Removes an instance's configuration row (when the instance is deleted). Returns false if absent.</summary>
    Task<bool> DeleteByInstanceAsync(Guid instanceId, CancellationToken ct = default);
}
