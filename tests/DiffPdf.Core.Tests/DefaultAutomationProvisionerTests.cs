using DiffPdf.Core.Models;
using DiffPdf.Messaging.Automation;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class DefaultAutomationProvisionerTests
{
    private readonly InMemoryScheduleStore _schedules = new();
    private readonly InMemoryWatchStore _watches = new();
    private readonly StubBatchLauncher _launcher = new();

    private static readonly Branch TheBranch = new() { Id = Guid.NewGuid(), Key = "alfa", Name = "alfa" };
    private static readonly ComparisonInstance TheInstance = new()
    {
        Id = Guid.NewGuid(), BranchId = TheBranch.Id, Key = "inst1", Name = "inst1", BasePath = @"C:\root\alfa\inst1",
    };

    private DefaultAutomationProvisioner Provisioner(Action<DefaultAutomationOptions>? configure = null)
    {
        var opt = new DefaultAutomationOptions { Enabled = true };
        configure?.Invoke(opt);
        return new DefaultAutomationProvisioner(_schedules, _watches, _launcher,
            Options.Create(opt), NullLogger<DefaultAutomationProvisioner>.Instance);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var result = await Provisioner(o => o.Enabled = false).ProvisionAsync(TheBranch, TheInstance);

        Assert.Equal(DefaultAutomationResult.None, result);
        Assert.Empty(await _schedules.ListByInstanceAsync(TheInstance.Id));
        Assert.Null(await _watches.GetByInstanceAsync(TheInstance.Id));
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task Enabled_CreatesDisabledScheduleAndWatch_AndFiresTrigger()
    {
        _launcher.Result = new LaunchResult(LaunchOutcome.Launched, Guid.NewGuid());

        var result = await Provisioner().ProvisionAsync(TheBranch, TheInstance);

        Assert.True(result.ScheduleCreated);
        Assert.True(result.WatchCreated);
        Assert.True(result.TriggerLaunched);

        var schedule = await _schedules.GetByKeyAsync(TheInstance.Id, "default");
        Assert.NotNull(schedule);
        Assert.False(schedule!.Enabled);            // created as a disabled template
        Assert.Equal("0 2 * * *", schedule.Cron);

        var watch = await _watches.GetByInstanceAsync(TheInstance.Id);
        Assert.NotNull(watch);
        Assert.False(watch!.Enabled);               // created disarmed

        var call = Assert.Single(_launcher.Calls);
        Assert.Equal(("alfa", "inst1"), call);
    }

    [Fact]
    public async Task SecondRun_DoesNotDuplicateScheduleOrWatch()
    {
        await Provisioner(o => o.FireInitialTrigger = false).ProvisionAsync(TheBranch, TheInstance);
        var second = await Provisioner(o => o.FireInitialTrigger = false).ProvisionAsync(TheBranch, TheInstance);

        Assert.False(second.ScheduleCreated);
        Assert.False(second.WatchCreated);
        Assert.Single(await _schedules.ListByInstanceAsync(TheInstance.Id));
    }

    [Fact]
    public async Task InvalidCron_SkipsScheduleButStillCreatesWatch()
    {
        var result = await Provisioner(o => { o.ScheduleCron = "not a cron"; o.FireInitialTrigger = false; })
            .ProvisionAsync(TheBranch, TheInstance);

        Assert.False(result.ScheduleCreated);
        Assert.True(result.WatchCreated);
        Assert.Empty(await _schedules.ListByInstanceAsync(TheInstance.Id));
    }

    [Fact]
    public async Task NothingToCompare_ReportsTriggerNotLaunched_ButStillAttempted()
    {
        _launcher.Result = new LaunchResult(LaunchOutcome.NothingToCompare);

        var result = await Provisioner(o => { o.CreateSchedule = false; o.CreateWatch = false; })
            .ProvisionAsync(TheBranch, TheInstance);

        Assert.False(result.TriggerLaunched);
        Assert.Single(_launcher.Calls); // attempted, but the readiness gate launched nothing
    }
}
