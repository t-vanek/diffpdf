using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.Triggers;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class TriggerTests
{
    // A launcher that always "launches" with a fresh job id, recording the spec it was handed.
    private sealed class FakeBatchLauncher : IBatchLauncher
    {
        public LaunchOutcome Outcome { get; set; } = LaunchOutcome.Launched;
        public LaunchSpec? LastSpec { get; private set; }
        public Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, CancellationToken ct = default)
        {
            LastSpec = spec;
            return Task.FromResult(Outcome == LaunchOutcome.Launched
                ? new LaunchResult(LaunchOutcome.Launched, Guid.NewGuid())
                : new LaunchResult(Outcome, null, "nope"));
        }
    }

    private static async Task<(TriggerService Svc, InMemoryTriggerStore Triggers, InMemoryTriggerRunStore Runs, FakeBatchLauncher Launcher)> BuildAsync()
    {
        var triggers = new InMemoryTriggerStore();
        var runs = new InMemoryTriggerRunStore();
        var launcher = new FakeBatchLauncher();
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var b = await branches.CreateAsync("Alfa", "Alfa");
        await instances.CreateAsync(b.Id, "Lama", "Lama", "/base", null);
        var svc = new TriggerService(triggers, runs, new InMemoryAuditLogStore(), launcher, branches, instances,
            NullLogger<TriggerService>.Instance);
        return (svc, triggers, runs, launcher);
    }

    [Fact]
    public async Task Run_CreatesJobAndRun_AndThreadsTriggerAndSource()
    {
        var (svc, _, runs, launcher) = await BuildAsync();
        var t = await svc.CreateAsync(new CreateTriggerInput { BranchKey = "Alfa", InstanceKey = "Lama", Name = "T" }, "tester", JobSource.RestApi);

        var r = await svc.RunAsync(t.Id, JobSource.RestApi, "tester", idempotencyKey: null);

        Assert.True(r.Success);
        Assert.NotNull(r.BatchJobId);
        Assert.Equal("queued", r.Status);
        Assert.Equal(t.Id, launcher.LastSpec!.TriggerId);     // launch carries the trigger + source
        Assert.Equal(JobSource.RestApi, launcher.LastSpec.Source);
        Assert.Single(await runs.ListByTriggerAsync(t.Id));
    }

    [Fact]
    public async Task Run_DisabledTrigger_FailsWithCode()
    {
        var (svc, _, _, _) = await BuildAsync();
        var t = await svc.CreateAsync(new CreateTriggerInput { BranchKey = "Alfa", InstanceKey = "Lama", Name = "T", Enabled = false }, "tester", JobSource.RestApi);

        var r = await svc.RunAsync(t.Id, JobSource.RestApi, "tester", null);

        Assert.False(r.Success);
        Assert.Equal("TRIGGER_DISABLED", r.ErrorCode);
    }

    [Fact]
    public async Task Run_WithIdempotencyKey_ReturnsSameJob()
    {
        var (svc, _, _, _) = await BuildAsync();
        var t = await svc.CreateAsync(new CreateTriggerInput { BranchKey = "Alfa", InstanceKey = "Lama", Name = "T" }, "tester", JobSource.RestApi);

        var first = await svc.RunAsync(t.Id, JobSource.RestApi, "tester", "key-1");
        var second = await svc.RunAsync(t.Id, JobSource.RestApi, "tester", "key-1");

        Assert.True(first.Success);
        Assert.Equal(first.BatchJobId, second.BatchJobId); // deduped — no second job despite the fake minting fresh ids
    }

    [Fact]
    public async Task Delete_IsSoftAndKeepsRunHistory()
    {
        var (svc, triggers, runs, _) = await BuildAsync();
        var t = await svc.CreateAsync(new CreateTriggerInput { BranchKey = "Alfa", InstanceKey = "Lama", Name = "T" }, "tester", JobSource.RestApi);
        await svc.RunAsync(t.Id, JobSource.RestApi, "tester", null);

        await svc.DeleteAsync(t.Id, "tester", JobSource.RestApi);

        var deleted = await triggers.GetAsync(t.Id);
        Assert.True(deleted!.IsDeleted);
        Assert.Equal(TriggerStatus.Deleted, deleted.Status);
        Assert.NotEmpty(await runs.ListByTriggerAsync(t.Id)); // history preserved

        var run = await svc.RunAsync(t.Id, JobSource.RestApi, "tester", null);
        Assert.False(run.Success);
        Assert.Equal("TRIGGER_DELETED", run.ErrorCode);
    }

    [Fact]
    public async Task Store_RejectsSecondDefaultForInstance()
    {
        var store = new InMemoryTriggerStore();
        var instanceId = Guid.NewGuid();
        Trigger Default() => new()
        {
            Id = Guid.NewGuid(), BranchId = Guid.NewGuid(), InstanceId = instanceId,
            BranchKey = "Alfa", InstanceKey = "Lama", Name = "Výchozí", IsDefault = true,
        };
        await store.CreateAsync(Default());
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Default()));
    }

    [Fact]
    public async Task Provisioner_DefaultTrigger_IsIdempotent()
    {
        var triggers = new InMemoryTriggerStore();
        var prov = new TriggerProvisioner(triggers, new InMemoryAuditLogStore(),
            new InMemoryBranchStore(), new InMemoryInstanceStore(), NullLogger<TriggerProvisioner>.Instance);
        var branchId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        await prov.EnsureDefaultTriggerAsync(branchId, "Alfa", instanceId, "Lama", "system");
        await prov.EnsureDefaultTriggerAsync(branchId, "Alfa", instanceId, "Lama", "system"); // again — must not duplicate

        var list = await triggers.ListAsync(new TriggerQuery { InstanceId = instanceId });
        Assert.Single(list);
        Assert.True(list[0].IsDefault);
    }
}
