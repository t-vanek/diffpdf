using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Persistence for business instances.</summary>
public interface IBusinessInstanceStore
{
    /// <summary>Creates a business instance; throws DuplicateKeyException if the key exists.</summary>
    Task<BusinessInstance> CreateAsync(string key, string name, CancellationToken ct = default);
    Task<BusinessInstance?> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<BusinessInstance>> ListAsync(CancellationToken ct = default);
}

/// <summary>Persistence for projects under a business instance.</summary>
public interface IProjectStore
{
    /// <summary>Creates a project; throws DuplicateKeyException if the key exists in the instance.</summary>
    Task<ComparisonProject> CreateAsync(Guid businessInstanceId, string key, string name, CancellationToken ct = default);
    Task<ComparisonProject?> GetByKeyAsync(Guid businessInstanceId, string key, CancellationToken ct = default);
    Task<IReadOnlyList<ComparisonProject>> ListAsync(Guid businessInstanceId, CancellationToken ct = default);
}
