using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryScheduleRunStoreTests
{
    [Fact]
    public async Task Record_Then_CompleteByJob_PatchesOutcome()
    {
        var store = new InMemoryScheduleRunStore();
        var scheduleId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await store.RecordStartAsync(scheduleId, jobId, DateTimeOffset.UtcNow);

        var pending = await store.ListByScheduleAsync(scheduleId);
        Assert.Single(pending);
        Assert.Equal(ScheduleRunOutcome.Pending, pending[0].Outcome);

        await store.CompleteByJobAsync(jobId, ScheduleRunOutcome.Passed, differing: 2, errors: 0,
            filesWithContentErrors: 0, passed: true, gateViolations: [], error: null, completedAt: DateTimeOffset.UtcNow);

        var done = await store.ListByScheduleAsync(scheduleId);
        Assert.Equal(ScheduleRunOutcome.Passed, done[0].Outcome);
        Assert.Equal(2, done[0].Differing);
        Assert.NotNull(done[0].CompletedAt);
    }

    [Fact]
    public async Task CompleteByJob_UnknownJob_IsNoOp()
    {
        var store = new InMemoryScheduleRunStore();
        // No run row for this job (e.g. a trigger / folder-watch launch) — must not throw.
        await store.CompleteByJobAsync(Guid.NewGuid(), ScheduleRunOutcome.Failed, 0, 0, 0, false, [], "x", DateTimeOffset.UtcNow);
        Assert.Empty(await store.ListByScheduleAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListBySchedule_NewestFirst()
    {
        var store = new InMemoryScheduleRunStore();
        var scheduleId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await store.RecordStartAsync(scheduleId, Guid.NewGuid(), t0);
        await store.RecordStartAsync(scheduleId, Guid.NewGuid(), t0.AddMinutes(5));

        var runs = await store.ListByScheduleAsync(scheduleId);
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].StartedAt >= runs[1].StartedAt);
    }

    [Fact]
    public async Task RecordStart_DuplicateJob_Idempotent()
    {
        var store = new InMemoryScheduleRunStore();
        var scheduleId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await store.RecordStartAsync(scheduleId, jobId, DateTimeOffset.UtcNow);
        await store.RecordStartAsync(scheduleId, jobId, DateTimeOffset.UtcNow);
        Assert.Single(await store.ListByScheduleAsync(scheduleId));
    }
}
