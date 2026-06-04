using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Observability;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>Publishes the <see cref="RunBatchComparison"/> that releases a queued job — a seam over Wolverine's bus (testable).</summary>
public interface IRunCommandPublisher
{
    Task PublishRunAsync(RunBatchComparison command, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class WolverineRunCommandPublisher(IMessageBus bus) : IRunCommandPublisher
{
    public Task PublishRunAsync(RunBatchComparison command, CancellationToken ct = default) => bus.PublishAsync(command).AsTask();
}

/// <summary>
/// Enforces the per-branch sequential queue: at most one active (Queued/Running/Paused) job per branch.
/// When the branch is idle and not held, releases the next pending (Draft) job — highest priority, then
/// oldest — by transitioning it Draft → Queued and publishing its <see cref="RunBatchComparison"/>. Called
/// after enqueue, after a job reaches a terminal state, after a cancel, and by a leader-gated timer (which
/// also makes the queue survive restarts).
/// </summary>
public interface IBranchQueueDispatcher
{
    /// <summary>Release the next pending job for the branch if it is idle and not paused, then publish the branch state.</summary>
    Task DispatchBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Recompute and publish the branch's queue state without releasing anything.</summary>
    Task PublishStateAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>The branch's current queue state (null if the branch does not exist). Does not release or publish.</summary>
    Task<BranchQueueState?> GetStateAsync(Guid branchId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class BranchQueueDispatcher(
    IJobStore jobs,
    IBranchStore branches,
    IRunCommandPublisher runPublisher,
    IBranchQueueStatePublisher statePublisher,
    BranchDispatchLocks locks,
    DiffPdfMetrics metrics,
    ILogger<BranchQueueDispatcher> logger) : IBranchQueueDispatcher
{
    public async Task DispatchBranchAsync(Guid branchId, CancellationToken ct = default)
    {
        // Best-effort: a transient failure here must never fail the caller's action or abort a tick — the
        // timer (and the next completion/cancel) re-attempts. Only cancellation propagates.
        try
        {
            var branch = await branches.GetByIdAsync(branchId, ct);
            if (branch is null)
                return;

            // Hold the queue while paused. Otherwise, under the per-branch lock so two callers can't both
            // release: if the branch has no running/paused job, re-kick an already-released (Queued) job, or
            // promote the next pending (Draft). "Occupied" is Running/Paused only — a Queued job is in-flight
            // (released but not yet claimed, e.g. held by a pause window) and is re-kicked, never duplicated.
            if (!branch.QueuePaused)
            {
                var gate = locks.For(branchId);
                await gate.WaitAsync(ct);
                try
                {
                    var list = await jobs.ListActiveAndDraftByBranchAsync(branchId, ct);
                    bool occupied = list.Any(j => j.Status is JobStatus.Running or JobStatus.Paused);
                    if (!occupied)
                    {
                        var queued = Pick(list, JobStatus.Queued);
                        if (queued is not null)
                        {
                            await runPublisher.PublishRunAsync(new RunBatchComparison(queued.Id, queued.BranchKey, queued.InstanceKey), ct);
                        }
                        else if (Pick(list, JobStatus.Draft) is { } draft && await jobs.EnqueueAsync(draft.Id, ct) is { } released)
                        {
                            await runPublisher.PublishRunAsync(new RunBatchComparison(released.Id, released.BranchKey, released.InstanceKey), ct);
                            logger.LogInformation("Branch {Branch}: released job {JobId} for instance {Instance} (priority {Priority}).",
                                branch.Key, released.Id, released.InstanceKey, released.Priority);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            await PublishStateAsync(branchId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Branch {BranchId}: dispatch failed (will retry on next tick / completion).", branchId);
        }
    }

    /// <summary>The highest-priority, then oldest job of a given status (the queue order).</summary>
    private static ComparisonJob? Pick(IEnumerable<ComparisonJob> list, JobStatus status) =>
        list.Where(j => j.Status == status).OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt).FirstOrDefault();

    public async Task PublishStateAsync(Guid branchId, CancellationToken ct = default)
    {
        if (await GetStateAsync(branchId, ct) is { } state)
            await statePublisher.PublishAsync(state, ct);
    }

    public async Task<BranchQueueState?> GetStateAsync(Guid branchId, CancellationToken ct = default)
    {
        var branch = await branches.GetByIdAsync(branchId, ct);
        return branch is null ? null : await BuildStateAsync(branch, ct);
    }

    private async Task<BranchQueueState> BuildStateAsync(Branch branch, CancellationToken ct)
    {
        var list = await jobs.ListActiveAndDraftByBranchAsync(branch.Id, ct);

        // One state per instance — the most relevant job (highest priority, then newest).
        var perInstance = list
            .GroupBy(j => j.InstanceKey)
            .Select(g => g.OrderByDescending(j => j.Priority).ThenByDescending(j => j.CreatedAt).First())
            .Select(j => new InstanceQueueState(j.BranchKey, j.InstanceKey, Map(j.Status), j.Id, j.Priority))
            .OrderBy(s => s.InstanceKey)
            .ToList();

        int pending = list.Count(j => j.Status == JobStatus.Draft);
        int queued = list.Count(j => j.Status == JobStatus.Queued);
        int running = list.Count(j => j.Status == JobStatus.Running);
        int paused = list.Count(j => j.Status == JobStatus.Paused);

        // Refresh the queue-depth gauge snapshot (read synchronously on /metrics scrape — never touches the DB).
        metrics.RecordBranchDepth(branch.Id, branch.Key, pending, queued, running, paused);

        return new BranchQueueState(branch.Key, branch.QueuePaused, pending, queued, running, paused, perInstance);
    }

    private static InstanceQueueStatus Map(JobStatus status) => status switch
    {
        JobStatus.Draft => InstanceQueueStatus.Pending,
        JobStatus.Queued => InstanceQueueStatus.Queued,
        JobStatus.Running => InstanceQueueStatus.Running,
        JobStatus.Paused => InstanceQueueStatus.Paused,
        _ => InstanceQueueStatus.Idle,
    };
}
