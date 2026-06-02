using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Persistence for branches (the top-level scope).</summary>
public interface IBranchStore
{
    /// <summary>Creates a branch; throws DuplicateKeyException if the key exists.</summary>
    Task<Branch> CreateAsync(string key, string name, CancellationToken ct = default);
    Task<Branch?> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default);
}

/// <summary>Persistence for instances under a branch.</summary>
public interface IInstanceStore
{
    /// <summary>Creates an instance; throws DuplicateKeyException if the key exists in the branch.</summary>
    Task<ComparisonInstance> CreateAsync(
        Guid branchId, string key, string name, string basePath, string? credentialProfile, CancellationToken ct = default);
    Task<ComparisonInstance?> GetByKeyAsync(Guid branchId, string key, CancellationToken ct = default);
    Task<IReadOnlyList<ComparisonInstance>> ListAsync(Guid branchId, CancellationToken ct = default);
}
