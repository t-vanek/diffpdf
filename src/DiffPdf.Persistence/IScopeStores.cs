using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Persistence for branches (the top-level scope).</summary>
public interface IBranchStore
{
    /// <summary>Creates a branch; throws DuplicateKeyException if the key exists.</summary>
    Task<Branch> CreateAsync(string key, string name, CancellationToken ct = default);

    /// <summary>
    /// Updates a branch's mutable fields (Name, Enabled) located by its (immutable) Key. Uses optimistic
    /// concurrency: throws ConcurrencyConflictException if the branch is gone or the version does not match.
    /// </summary>
    Task<Branch> UpdateAsync(Branch branch, long expectedVersion, CancellationToken ct = default);

    Task<Branch?> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<Branch?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default);

    /// <summary>Holds/releases the branch's sequential job queue (sets the QueuePaused flag). Best-effort, no version guard.</summary>
    Task SetQueuePausedAsync(Guid branchId, bool paused, CancellationToken ct = default);

    /// <summary>
    /// Deletes a branch by key; returns false if it does not exist. The caller must first ensure the
    /// branch has no instances and no active jobs (the API endpoint enforces this).
    /// </summary>
    Task<bool> DeleteByKeyAsync(string key, CancellationToken ct = default);
}

/// <summary>Persistence for instances under a branch.</summary>
public interface IInstanceStore
{
    /// <summary>Creates an instance; throws DuplicateKeyException if the key exists in the branch.</summary>
    Task<ComparisonInstance> CreateAsync(
        Guid branchId, string key, string name, string basePath, string? credentialProfile, CancellationToken ct = default);

    /// <summary>
    /// Updates an instance's mutable fields (Name, BasePath, CredentialProfile, Enabled) located by its
    /// (BranchId, Key). Uses optimistic concurrency: throws ConcurrencyConflictException on miss/version mismatch.
    /// </summary>
    Task<ComparisonInstance> UpdateAsync(ComparisonInstance instance, long expectedVersion, CancellationToken ct = default);

    Task<ComparisonInstance?> GetByKeyAsync(Guid branchId, string key, CancellationToken ct = default);
    Task<IReadOnlyList<ComparisonInstance>> ListAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Counts instances grouped by branch id in one query — for list views showing per-branch counts.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountByBranchAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes an instance by key within a branch; returns false if it does not exist. The caller must
    /// first ensure nothing references it (schedules / watches / jobs); the API endpoint enforces this.
    /// </summary>
    Task<bool> DeleteByKeyAsync(Guid branchId, string key, CancellationToken ct = default);
}
