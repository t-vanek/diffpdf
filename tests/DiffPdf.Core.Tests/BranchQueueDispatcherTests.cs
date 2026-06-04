using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Observability;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using DiffPdf.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// The per-branch sequential queue rules: release/re-kick one at a time, priority order, branch hold,
/// advance on free. "Occupied" = a Running/Paused job; a Queued (released, not-yet-claimed) job is re-kicked,
/// never duplicated.
/// </summary>
public class BranchQueueDispatcherTests
{
    private sealed class RecordingRunPublisher : IRunCommandPublisher
    {
        public List<RunBatchComparison> Published { get; } = [];
        public Task PublishRunAsync(RunBatchComparison command, CancellationToken ct = default)
        {
            Published.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed record Ctx(InMemoryJobStore Jobs, InMemoryBranchStore Branches, RecordingRunPublisher Pub, BranchQueueDispatcher Dispatcher);

    private static async Task<(Ctx Ctx, Branch Branch)> BuildAsync()
    {
        var jobs = new InMemoryJobStore();
        var branches = new InMemoryBranchStore();
        var pub = new RecordingRunPublisher();
        var dispatcher = new BranchQueueDispatcher(jobs, branches, pub, new NullBranchQueueStatePublisher(), new BranchDispatchLocks(), new DiffPdfMetrics(), NullLogger<BranchQueueDispatcher>.Instance);
        var branch = await branches.CreateAsync("Alfa", "Alfa");
        return (new Ctx(jobs, branches, pub, dispatcher), branch);
    }

    private static Task<ComparisonJob> DraftAsync(InMemoryJobStore jobs, Branch branch, string instanceKey, int priority = 0) =>
        jobs.CreateAsync(new ComparisonJob
        {
            Id = Guid.NewGuid(),
            BranchId = branch.Id,
            InstanceId = Guid.NewGuid(),
            Status = JobStatus.Draft,
            Priority = priority,
            Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instanceKey) },
        });

    private static async Task<ComparisonJob> QueuedAsync(InMemoryJobStore jobs, Branch branch, string instanceKey, int priority = 0)
    {
        var d = await DraftAsync(jobs, branch, instanceKey, priority);
        return (await jobs.EnqueueAsync(d.Id))!;
    }

    private static async Task<ComparisonJob> RunningAsync(InMemoryJobStore jobs, Branch branch, string instanceKey)
    {
        var d = await DraftAsync(jobs, branch, instanceKey);
        await jobs.EnqueueAsync(d.Id);
        return (await jobs.TryStartAsync(d.Id, "w", TimeSpan.FromMinutes(5)))!;
    }

    [Fact]
    public async Task PromotesOneDraft_AndHoldsTheRest()
    {
        var (c, branch) = await BuildAsync();
        await DraftAsync(c.Jobs, branch, "i1");
        await DraftAsync(c.Jobs, branch, "i2");

        await c.Dispatcher.DispatchBranchAsync(branch.Id);

        Assert.Single(c.Pub.Published);
        var released = await c.Jobs.GetAsync(c.Pub.Published[0].JobId);
        Assert.Equal(JobStatus.Queued, released!.Status);
        Assert.Equal(1, (await c.Jobs.ListActiveAndDraftByBranchAsync(branch.Id)).Count(j => j.Status == JobStatus.Draft));
    }

    [Fact]
    public async Task DoesNotRelease_WhileRunning()
    {
        var (c, branch) = await BuildAsync();
        await RunningAsync(c.Jobs, branch, "i1"); // occupies the branch
        await DraftAsync(c.Jobs, branch, "i2");

        await c.Dispatcher.DispatchBranchAsync(branch.Id);

        Assert.Empty(c.Pub.Published);
    }

    [Fact]
    public async Task ReKicksQueuedJob_RatherThanPromotingADraft()
    {
        var (c, branch) = await BuildAsync();
        var queued = await QueuedAsync(c.Jobs, branch, "i1"); // released but not yet claimed (e.g. held by a pause window)
        var draft = await DraftAsync(c.Jobs, branch, "i2");

        await c.Dispatcher.DispatchBranchAsync(branch.Id);

        Assert.Single(c.Pub.Published);
        Assert.Equal(queued.Id, c.Pub.Published[0].JobId); // re-kicked the Queued one
        Assert.Equal(JobStatus.Draft, (await c.Jobs.GetAsync(draft.Id))!.Status); // the Draft stayed pending
    }

    [Fact]
    public async Task PriorityRun_PromotedBeforePlainEnqueue()
    {
        var (c, branch) = await BuildAsync();
        await DraftAsync(c.Jobs, branch, "i1", priority: 0);
        var high = await DraftAsync(c.Jobs, branch, "i2", priority: 100);

        await c.Dispatcher.DispatchBranchAsync(branch.Id);

        Assert.Single(c.Pub.Published);
        Assert.Equal(high.Id, c.Pub.Published[0].JobId);
    }

    [Fact]
    public async Task BranchPaused_HoldsQueue_UntilResumed()
    {
        var (c, branch) = await BuildAsync();
        await DraftAsync(c.Jobs, branch, "i1");
        await c.Branches.SetQueuePausedAsync(branch.Id, true);

        await c.Dispatcher.DispatchBranchAsync(branch.Id);
        Assert.Empty(c.Pub.Published); // held even though no job is running

        await c.Branches.SetQueuePausedAsync(branch.Id, false);
        await c.Dispatcher.DispatchBranchAsync(branch.Id);
        Assert.Single(c.Pub.Published); // released after resume
    }

    [Fact]
    public async Task Cancel_AdvancesNext()
    {
        var (c, branch) = await BuildAsync();
        var running = await RunningAsync(c.Jobs, branch, "i1");
        var next = await DraftAsync(c.Jobs, branch, "i2");

        await c.Dispatcher.DispatchBranchAsync(branch.Id);
        Assert.Empty(c.Pub.Published); // occupied

        await c.Jobs.CancelAsync(running.Id);              // branch frees
        await c.Dispatcher.DispatchBranchAsync(branch.Id); // promotes the next draft

        Assert.Single(c.Pub.Published);
        Assert.Equal(next.Id, c.Pub.Published[0].JobId);
    }

    [Fact]
    public async Task ParallelBranches_BothRelease()
    {
        var (c, alfa) = await BuildAsync();
        var beta = await c.Branches.CreateAsync("Beta", "Beta");
        await DraftAsync(c.Jobs, alfa, "a1");
        await DraftAsync(c.Jobs, beta, "b1");

        await c.Dispatcher.DispatchBranchAsync(alfa.Id);
        await c.Dispatcher.DispatchBranchAsync(beta.Id);

        Assert.Equal(2, c.Pub.Published.Count);
    }
}
