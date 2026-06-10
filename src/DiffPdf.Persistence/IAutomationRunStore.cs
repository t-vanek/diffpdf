using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for automation run history. The runner records one row per attempt. A row's
/// <see cref="AutomationRun.AutomationId"/> is not a foreign key, so history outlives a deleted automation.
/// </summary>
public interface IAutomationRunStore
{
    /// <summary>Records one completed attempt.</summary>
    Task AddAsync(AutomationRun run, CancellationToken ct = default);

    /// <summary>Most recent runs for an automation, newest first.</summary>
    Task<IReadOnlyList<AutomationRun>> ListByAutomationAsync(Guid automationId, int limit = 50, CancellationToken ct = default);

    /// <summary>Bulk-deletes runs started before the cutoff (DB-row retention). Returns rows deleted.</summary>
    Task<int> DeleteStartedBeforeAsync(DateTimeOffset startedBefore, CancellationToken ct = default);
}
