using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for folder-watches — at most one per instance (unique by instance id). Runtime-managed
/// via the API: <see cref="UpsertAsync"/> creates or replaces an instance's watch (the watch id is
/// preserved across updates so the watcher's debounce state is stable).
/// </summary>
public interface IWatchStore
{
    Task<FolderWatch> UpsertAsync(FolderWatch watch, CancellationToken ct = default);

    Task<FolderWatch?> GetByInstanceAsync(Guid instanceId, CancellationToken ct = default);

    Task<bool> DeleteByInstanceAsync(Guid instanceId, CancellationToken ct = default);

    Task<IReadOnlyList<FolderWatch>> ListAsync(CancellationToken ct = default);

    /// <summary>All enabled watches — the watcher's per-tick hot path.</summary>
    Task<IReadOnlyList<FolderWatch>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>Records when a watch last launched a batch (best-effort).</summary>
    Task TouchLastTriggeredAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);
}
