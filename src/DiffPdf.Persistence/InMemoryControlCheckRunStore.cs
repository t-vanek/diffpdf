using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory control-check-run store for single-instance / dev use. Does not survive restart.</summary>
public sealed class InMemoryControlCheckRunStore : IControlCheckRunStore
{
    private readonly ConcurrentDictionary<Guid, ControlCheckRun> _byId = new();

    public Task AddAsync(ControlCheckRun run, CancellationToken ct = default)
    {
        _byId[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ControlCheckRun>> ListByCheckAsync(Guid checkId, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ControlCheckRun>>(
            _byId.Values.Where(r => r.CheckId == checkId)
                .OrderByDescending(r => r.StartedAt).Take(limit).ToList());
}
