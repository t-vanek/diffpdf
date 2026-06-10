using DiffPdf.Application.Configuration;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Automations;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Scheduled runs: the step executor enqueues a batch (enqueueOnly, source = Scheduler) for each enabled
/// instance in scope, and the schedule service upserts/deletes the backing ScheduledComparison automation.
/// </summary>
public class ScheduledComparisonTests
{
    private sealed class FakeLauncher : IBatchLauncher
    {
        public List<(string Branch, string Instance, bool EnqueueOnly, JobSource Source)> Calls { get; } = [];
        public Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, bool enqueueOnly = false, CancellationToken ct = default)
        {
            Calls.Add((branchKey, instanceKey, enqueueOnly, spec.Source));
            return Task.FromResult(new LaunchResult(LaunchOutcome.Launched, Guid.NewGuid()));
        }
    }

    private static Automation BranchScoped(string branchKey) => new()
    {
        Id = Guid.NewGuid(), Key = $"sched-{branchKey}", Name = "x",
        ScopeKind = AutomationScopeKind.Branch, BranchKey = branchKey, Cron = "0 6 * * *",
        Steps = [new AutomationStep { Type = AutomationStepType.ScheduledComparison }],
    };

    [Fact]
    public async Task Executor_enqueues_each_enabled_instance_in_branch_scope()
    {
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var b = await branches.CreateAsync("Alfa", "Alfa");
        await instances.CreateAsync(b.Id, "Lama", "Lama", "/base/lama", null);
        await instances.CreateAsync(b.Id, "Beta", "Beta", "/base/beta", null);

        var launcher = new FakeLauncher();
        var resolver = new ScopeConfigurationResolver(new InMemoryScopeConfigurationStore());
        var executor = new ScheduledComparisonStepExecutor(
            branches, instances, resolver, launcher, NullLogger<ScheduledComparisonStepExecutor>.Instance);

        var automation = BranchScoped("Alfa");
        var result = await executor.ExecuteAsync(automation, automation.Steps[0], CancellationToken.None);

        Assert.Equal(AutomationRunOutcome.Ok, result.Outcome);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.All(launcher.Calls, c => Assert.True(c.EnqueueOnly));               // queued, not run-now
        Assert.All(launcher.Calls, c => Assert.Equal(JobSource.Scheduler, c.Source));
        Assert.Contains(launcher.Calls, c => c is ("Alfa", "Lama", _, _));
        Assert.Contains(launcher.Calls, c => c is ("Alfa", "Beta", _, _));
    }

    [Fact]
    public async Task Executor_warns_when_no_instances_in_scope()
    {
        var branches = new InMemoryBranchStore();
        await branches.CreateAsync("Empty", "Empty");
        var executor = new ScheduledComparisonStepExecutor(
            branches, new InMemoryInstanceStore(), new ScopeConfigurationResolver(new InMemoryScopeConfigurationStore()),
            new FakeLauncher(), NullLogger<ScheduledComparisonStepExecutor>.Instance);

        var automation = BranchScoped("Empty");
        var result = await executor.ExecuteAsync(automation, automation.Steps[0], CancellationToken.None);
        Assert.Equal(AutomationRunOutcome.Warning, result.Outcome);
    }

    [Fact]
    public async Task Service_enables_then_disables_the_branch_schedule_automation()
    {
        var automations = new InMemoryAutomationStore();
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        await branches.CreateAsync("Alfa", "Alfa");
        var svc = new ScheduleService(automations, branches, instances);

        var enabled = await svc.SetBranchScheduleAsync("Alfa", true, "0 6 * * *");
        Assert.NotNull(enabled);
        Assert.True(enabled!.Enabled);
        Assert.Equal("0 6 * * *", enabled.Cron);

        var created = await automations.GetByKeyAsync("sched-Alfa");
        Assert.NotNull(created);
        Assert.Equal(AutomationStepType.ScheduledComparison, Assert.Single(created!.Steps).Type);
        Assert.Equal(AutomationScopeKind.Branch, created.ScopeKind);
        Assert.Equal("Alfa", created.BranchKey);
        Assert.True(created.Enabled);
        Assert.NotNull(created.NextRunAt); // seeded eagerly so the engine fires without a seed pass

        Assert.Equal("0 6 * * *", (await svc.GetBranchScheduleAsync("Alfa"))!.Cron);

        var disabled = await svc.SetBranchScheduleAsync("Alfa", false, "");
        Assert.False(disabled!.Enabled);
        Assert.Null(await automations.GetByKeyAsync("sched-Alfa")); // deleted, not orphaned
    }

    [Fact]
    public async Task Service_returns_null_for_unknown_scope()
    {
        var svc = new ScheduleService(new InMemoryAutomationStore(), new InMemoryBranchStore(), new InMemoryInstanceStore());
        Assert.Null(await svc.GetBranchScheduleAsync("Nope"));
        Assert.Null(await svc.SetBranchScheduleAsync("Nope", true, "0 6 * * *"));
    }

    [Fact]
    public async Task Service_rejects_invalid_cron()
    {
        var branches = new InMemoryBranchStore();
        await branches.CreateAsync("Alfa", "Alfa");
        var svc = new ScheduleService(new InMemoryAutomationStore(), branches, new InMemoryInstanceStore());

        await Assert.ThrowsAsync<ScheduleValidationException>(
            () => svc.SetBranchScheduleAsync("Alfa", true, "not a cron"));
    }
}
