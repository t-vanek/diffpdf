using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Automations;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class AutomationProvisionerTests
{
    private static AutomationProvisioner Build(
        IAutomationStore automations, IBranchStore branches,
        AutoProvisionOptions? auto = null, string? scopeRoot = null) =>
        new(automations, branches,
            Options.Create(new AutomationEngineOptions { AutoProvision = auto ?? new AutoProvisionOptions() }),
            Options.Create(new ScopeSyncOptions { RootPath = scopeRoot }),
            NullLogger<AutomationProvisioner>.Instance);

    [Fact]
    public async Task EnsureBranchAutomations_CreatesBranchScopedReadiness_WithSeededSchedule()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore());

        await provisioner.EnsureBranchAutomationsAsync("Alfa");

        var automation = await store.GetByKeyAsync("readiness-Alfa");
        Assert.NotNull(automation);
        Assert.Equal(AutomationStepType.Readiness, Assert.Single(automation!.Steps).Type);
        Assert.Equal(AutomationScopeKind.Branch, automation.ScopeKind);
        Assert.Equal("Alfa", automation.BranchKey);
        Assert.Equal(300, automation.IntervalSeconds);
        Assert.NotNull(automation.NextRunAt); // seeded eagerly, no engine pass needed
        Assert.Contains(NotificationEvent.ReadinessFailed, automation.Events);
        Assert.Contains(NotificationEvent.AutomationRecovered, automation.Events);
    }

    [Fact]
    public async Task EnsureBranchAutomations_IsIdempotent()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore());

        await provisioner.EnsureBranchAutomationsAsync("Alfa");
        await provisioner.EnsureBranchAutomationsAsync("Alfa");

        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task EnsureBranchAutomations_DoesNotOverwriteAnEditedOrDisabledAutomation()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore());
        await provisioner.EnsureBranchAutomationsAsync("Alfa");
        var original = await store.GetByKeyAsync("readiness-Alfa");
        await store.UpdateAsync(original! with { Enabled = false, IntervalSeconds = 30 }, original!.Version);

        await provisioner.EnsureBranchAutomationsAsync("Alfa"); // must be a no-op

        var after = await store.GetByKeyAsync("readiness-Alfa");
        Assert.False(after!.Enabled);
        Assert.Equal(30, after.IntervalSeconds);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task EnsureBranchAutomations_WhenDisabled_IsNoOp()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore(), new AutoProvisionOptions { Enabled = false });

        await provisioner.EnsureBranchAutomationsAsync("Alfa");

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task EnsureBranchAutomations_OverlongKey_SkipsWithoutThrowing()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore());
        string longBranch = new('a', 64); // "readiness-" + 64 chars exceeds the 64-char key limit

        await provisioner.EnsureBranchAutomationsAsync(longBranch); // must not throw

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ProvisionBaseline_WithoutScopeRoot_CreatesHealthAndRetentionOnly()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore()); // no scope root

        await provisioner.ProvisionBaselineAndExistingAsync();

        var health = await store.GetByKeyAsync("health");
        Assert.Equal(AutomationStepType.Health, Assert.Single(health!.Steps).Type);
        Assert.NotNull(health.NextRunAt);

        var retention = await store.GetByKeyAsync("retention");
        var retentionStep = Assert.Single(retention!.Steps);
        Assert.Equal(AutomationStepType.Retention, retentionStep.Type);
        Assert.Equal("30", retentionStep.Parameters["retentionDays"]);
        Assert.Equal("0 3 * * *", retention.Cron);

        var dbRetention = await store.GetByKeyAsync("db-row-retention");
        Assert.Equal(AutomationStepType.DbRowRetention, Assert.Single(dbRetention!.Steps).Type);

        Assert.Null(await store.GetByKeyAsync("structure-sync")); // skipped: no ScopeSync root
    }

    [Fact]
    public async Task ProvisionBaseline_WithScopeRoot_AlsoCreatesStructureSync()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore(), scopeRoot: @"\\srv\root");

        await provisioner.ProvisionBaselineAndExistingAsync();

        var sync = await store.GetByKeyAsync("structure-sync");
        Assert.Equal(AutomationStepType.StructureSync, Assert.Single(sync!.Steps).Type);
    }

    [Fact]
    public async Task ProvisionBaseline_BackfillsReadinessForEveryExistingBranch()
    {
        var store = new InMemoryAutomationStore();
        var branches = new InMemoryBranchStore();
        await branches.CreateAsync("Alfa", "Alfa");
        await branches.CreateAsync("Beta", "Beta");
        var provisioner = Build(store, branches);

        await provisioner.ProvisionBaselineAndExistingAsync();

        Assert.NotNull(await store.GetByKeyAsync("readiness-Alfa"));
        Assert.NotNull(await store.GetByKeyAsync("readiness-Beta"));
    }

    [Fact]
    public async Task RemoveBranchAutomations_DeletesTheReadinessAutomation()
    {
        var store = new InMemoryAutomationStore();
        var provisioner = Build(store, new InMemoryBranchStore());
        await provisioner.EnsureBranchAutomationsAsync("Alfa");

        await provisioner.RemoveBranchAutomationsAsync("Alfa");

        Assert.Null(await store.GetByKeyAsync("readiness-Alfa"));
    }
}
