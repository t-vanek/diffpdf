using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for control-check run history. The runner records one row per execution. A row's
/// <see cref="ControlCheckRun.CheckId"/> is not a foreign key, so history outlives a deleted check.
/// </summary>
public interface IControlCheckRunStore
{
    /// <summary>Records one completed (or in-flight) check run.</summary>
    Task AddAsync(ControlCheckRun run, CancellationToken ct = default);

    /// <summary>Most recent runs for a check, newest first.</summary>
    Task<IReadOnlyList<ControlCheckRun>> ListByCheckAsync(Guid checkId, int limit = 50, CancellationToken ct = default);
}
