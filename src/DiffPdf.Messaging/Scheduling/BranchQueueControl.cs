using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Persistence;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Application-level queue control for the desktop manager: translates the per-instance / per-branch
/// actions (Enqueue / Run / Pause / Resume / Stop) into job-store transitions + dispatcher releases, and
/// returns the resulting <see cref="QueueActionOutcome"/> (state + an optional note). Enqueue/Run go through
/// <see cref="IBatchLauncher"/> (Draft + dispatch) and never double-enqueue an instance that already has a
/// pending/active job; Pause/Resume/Stop act on the instance's single non-terminal job; branch-level actions
/// cascade (hold flag + every active job).
/// </summary>
public interface IBranchQueueControl
{
    Task<QueueActionOutcome?> ActOnInstanceAsync(string branchKey, string instanceKey, QueueAction action, CancellationToken ct = default);
    Task<QueueActionOutcome?> ActOnBranchAsync(string branchKey, QueueAction action, CancellationToken ct = default);
    Task<BranchQueueState?> GetStateAsync(string branchKey, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class BranchQueueControl(
    IBranchStore branches,
    IInstanceStore instances,
    IJobStore jobStore,
    IBatchLauncher launcher,
    IBranchQueueDispatcher dispatcher,
    IScopeConfigurationResolver configResolver,
    IJobResumeService resume) : IBranchQueueControl
{
    private static readonly JobStatus[] NonTerminal =
        [JobStatus.Draft, JobStatus.Queued, JobStatus.Running, JobStatus.Paused];

    public async Task<BranchQueueState?> GetStateAsync(string branchKey, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        return branch is null ? null : await dispatcher.GetStateAsync(branch.Id, ct);
    }

    public async Task<QueueActionOutcome?> ActOnInstanceAsync(string branchKey, string instanceKey, QueueAction action, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null) return null;

        string? message = null;
        switch (action)
        {
            case QueueAction.Enqueue:
            case QueueAction.Run:
                message = await TryEnqueueAsync(branch.Id, branch.Key, instance.Id, instance.Key, action, ct);
                await dispatcher.DispatchBranchAsync(branch.Id, ct);
                break;

            case QueueAction.Pause:
                if (await ActiveJobAsync(branchKey, instanceKey, ct) is { } pj)
                    await jobStore.PauseAsync(pj.Id, ct);
                await dispatcher.PublishStateAsync(branch.Id, ct);
                break;

            case QueueAction.Resume:
                if (await ActiveJobAsync(branchKey, instanceKey, ct) is { } rj)
                    await resume.ResumeAsync(rj.Id, ct);
                await dispatcher.PublishStateAsync(branch.Id, ct);
                break;

            case QueueAction.Stop:
                if (await ActiveJobAsync(branchKey, instanceKey, ct) is { } sj)
                    await jobStore.CancelAsync(sj.Id, ct);
                await dispatcher.DispatchBranchAsync(branch.Id, ct);
                break;
        }

        return await OutcomeAsync(branch.Id, message, ct);
    }

    public async Task<QueueActionOutcome?> ActOnBranchAsync(string branchKey, QueueAction action, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;

        string? message = null;
        switch (action)
        {
            case QueueAction.Enqueue:
            case QueueAction.Run:
                int launched = 0;
                var skipped = new List<string>();
                foreach (var inst in (await instances.ListAsync(branch.Id, ct)).Where(i => i.Enabled))
                {
                    // Best-effort: one instance's failure (or skip) never aborts the fan-out over the branch.
                    try
                    {
                        if (await TryEnqueueAsync(branch.Id, branch.Key, inst.Id, inst.Key, action, ct) is null) launched++;
                        else skipped.Add(inst.Key);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { skipped.Add(inst.Key); }
                }
                await dispatcher.DispatchBranchAsync(branch.Id, ct);
                message = $"Zařazeno: {launched}." + (skipped.Count > 0 ? $" Přeskočeno: {string.Join(", ", skipped)}." : "");
                break;

            case QueueAction.Pause:
                await branches.SetQueuePausedAsync(branch.Id, true, ct);
                foreach (var j in await jobStore.ListActiveAndDraftByBranchAsync(branch.Id, ct))
                    if (j.Status == JobStatus.Running)
                        await SafeAsync(() => jobStore.PauseAsync(j.Id, ct));
                await dispatcher.PublishStateAsync(branch.Id, ct);
                break;

            case QueueAction.Resume:
                await branches.SetQueuePausedAsync(branch.Id, false, ct);
                foreach (var j in await jobStore.ListActiveAndDraftByBranchAsync(branch.Id, ct))
                    if (j.Status == JobStatus.Paused)
                        await SafeAsync(() => resume.ResumeAsync(j.Id, ct));
                await dispatcher.DispatchBranchAsync(branch.Id, ct);
                break;

            case QueueAction.Stop:
                await branches.SetQueuePausedAsync(branch.Id, false, ct);
                // Cancel the running job AND every pending Draft/Queued — clears the rest of the branch queue.
                foreach (var j in await jobStore.ListActiveAndDraftByBranchAsync(branch.Id, ct))
                    await SafeAsync(() => jobStore.CancelAsync(j.Id, ct));
                await dispatcher.DispatchBranchAsync(branch.Id, ct);
                break;
        }

        return await OutcomeAsync(branch.Id, message, ct);
    }

    private async Task<QueueActionOutcome?> OutcomeAsync(Guid branchId, string? message, CancellationToken ct)
    {
        var state = await dispatcher.GetStateAsync(branchId, ct);
        return state is null ? null : new QueueActionOutcome(state, message);
    }

    /// <summary>Enqueues one instance (Draft). Returns null on success, or a note when it was skipped. Never double-enqueues.</summary>
    private async Task<string?> TryEnqueueAsync(Guid branchId, string branchKey, Guid instanceId, string instanceKey, QueueAction action, CancellationToken ct)
    {
        // Anti-duplicate: at most one pending/active job per instance (the sequential model assumes this).
        if (await ActiveJobAsync(branchKey, instanceKey, ct) is not null)
            return $"Instance '{instanceKey}' už je ve frontě nebo běží.";

        var eff = await configResolver.ResolveForInstanceAsync(branchId, instanceId, ct);
        var spec = LaunchSpec.FromEffective(eff, triggerId: null, JobSource.Manager, priority: action == QueueAction.Run ? 100 : 0);
        var result = await launcher.LaunchAsync(branchKey, instanceKey, spec, enqueueOnly: true, ct: ct);
        return result.Outcome switch
        {
            LaunchOutcome.Launched => null,
            LaunchOutcome.NothingToCompare => $"Instance '{instanceKey}': není co porovnávat.",
            LaunchOutcome.Unreachable => $"Instance '{instanceKey}': základní cesta není dostupná.",
            _ => $"Instance '{instanceKey}': nelze spustit (zakázaná?).",
        };
    }

    /// <summary>Runs a per-job cascade step best-effort: one job's failure must not leave a half-applied branch action.</summary>
    private static async Task SafeAsync(Func<Task> op)
    {
        try { await op(); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort cascade */ }
    }

    /// <summary>The instance's single non-terminal job (sequential model: at most one per instance), or null.</summary>
    private async Task<ComparisonJob?> ActiveJobAsync(string branchKey, string instanceKey, CancellationToken ct)
    {
        var list = await jobStore.ListAsync(new JobListQuery { BranchKey = branchKey, InstanceKey = instanceKey, Limit = 50 }, ct);
        return list.FirstOrDefault(j => NonTerminal.Contains(j.Status));
    }
}
