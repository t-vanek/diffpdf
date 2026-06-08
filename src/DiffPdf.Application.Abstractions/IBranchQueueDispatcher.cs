using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Releases the next pending job for a branch (one at a time) and publishes the branch's queue state.
/// Implemented over the durable per-branch queue; consumed by the API, the queue control and the handlers.
/// </summary>
public interface IBranchQueueDispatcher
{
    /// <summary>Release the next pending job for the branch if it is idle and not paused, then publish the branch state.</summary>
    Task DispatchBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Recompute and publish the branch's queue state without releasing anything.</summary>
    Task PublishStateAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>The branch's current queue state (null if the branch does not exist). Does not release or publish.</summary>
    Task<BranchQueueState?> GetStateAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Builds queue state for many branches with a single jobs query (avoids a per-branch round-trip).</summary>
    Task<IReadOnlyDictionary<Guid, BranchQueueState>> GetStatesAsync(IReadOnlyList<Branch> branches, CancellationToken ct = default);
}
