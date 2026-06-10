using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory automation-run store for single-instance / dev use. Does not survive restart.</summary>
public sealed class InMemoryAutomationRunStore : IAutomationRunStore
{
    private readonly ConcurrentDictionary<Guid, AutomationRun> _byId = new();

    public Task AddAsync(AutomationRun run, CancellationToken ct = default)
    {
        _byId[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AutomationRun>> ListByAutomationAsync(Guid automationId, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AutomationRun>>(
            _byId.Values.Where(r => r.AutomationId == automationId)
                .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Attempt).Take(limit).ToList());

    public Task<int> DeleteStartedBeforeAsync(DateTimeOffset startedBefore, CancellationToken ct = default)
    {
        int deleted = 0;
        foreach (var run in _byId.Values.Where(r => r.StartedAt < startedBefore).ToList())
        {
            if (_byId.TryRemove(run.Id, out _))
                deleted++;
        }
        return Task.FromResult(deleted);
    }
}
