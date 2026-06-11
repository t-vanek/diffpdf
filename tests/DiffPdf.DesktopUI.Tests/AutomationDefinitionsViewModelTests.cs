using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The Automatizace summary strip and the steps editor of the automation definitions view-model.</summary>
public class AutomationDefinitionsViewModelTests
{
    private static AutomationDefinitionsViewModel NewVm() =>
        new(new ServerSession(), new DialogService(new ToastService()));

    [Fact]
    public void Summary_counts_automation_health_from_the_list()
    {
        var vm = NewVm();
        vm.Automations.Add(new AutomationResponse { Id = Guid.NewGuid(), Enabled = true, LastOutcome = AutomationRunOutcome.Ok });
        vm.Automations.Add(new AutomationResponse { Id = Guid.NewGuid(), Enabled = true, LastOutcome = AutomationRunOutcome.Failed });
        vm.Automations.Add(new AutomationResponse { Id = Guid.NewGuid(), Enabled = false, LastOutcome = null });

        var byLabel = vm.Summary.ToDictionary(l => l.Label, l => l.Count);
        Assert.Equal(3, byLabel["Celkem"]);
        Assert.Equal(1, byLabel["OK"]);
        Assert.Equal(1, byLabel["Selhané"]);
        Assert.Equal(1, byLabel["Nespuštěné"]);
        Assert.Equal(1, byLabel["Zakázané"]);
        Assert.Equal(0, byLabel["Varování"]);
    }

    [Fact]
    public void StepsEditor_starts_with_one_step_and_keeps_at_least_one()
    {
        var vm = NewVm();
        var only = Assert.Single(vm.Steps);

        vm.RemoveStepCommand.Execute(only); // must be a no-op — a pipeline needs at least one step
        Assert.Single(vm.Steps);

        vm.AddStepCommand.Execute(null);
        Assert.Equal(2, vm.Steps.Count);
        vm.RemoveStepCommand.Execute(vm.Steps[0]);
        Assert.Single(vm.Steps);
    }

    [Fact]
    public void StepsEditor_moves_steps_up_and_down()
    {
        var vm = NewVm();
        vm.Steps[0].Type = AutomationStepType.Health;
        vm.AddStepCommand.Execute(null);
        vm.Steps[1].Type = AutomationStepType.Readiness;

        vm.MoveStepUpCommand.Execute(vm.Steps[1]);
        Assert.Equal(AutomationStepType.Readiness, vm.Steps[0].Type);

        vm.MoveStepDownCommand.Execute(vm.Steps[0]);
        Assert.Equal(AutomationStepType.Health, vm.Steps[0].Type);
    }

    [Fact]
    public void StepRow_emits_typed_parameters_and_blank_name_to_null()
    {
        ParameterSpecProvider specs = type => type == AutomationStepType.Retention
            ?
            [
                new AutomationParameterSpecResponse { Key = "retentionDays", Label = "Dny", Type = AutomationParameterType.Int, Default = "30" },
                new AutomationParameterSpecResponse { Key = "maxPerTick", Label = "Max", Type = AutomationParameterType.Int, Default = "100" },
            ]
            : [];

        var row = new AutomationStepRowViewModel(specs, AutomationStepType.Retention, "  ",
            new Dictionary<string, string> { ["retentionDays"] = "45", ["maxPerTick"] = "100" });

        var input = row.ToInput();
        Assert.Equal(AutomationStepType.Retention, input.Type);
        Assert.Null(input.Name);
        Assert.Equal("45", input.Parameters!["retentionDays"]); // seeded value populated the typed field
        Assert.Equal("100", input.Parameters!["maxPerTick"]);
    }

    [Fact]
    public void StepRow_preserves_unknown_parameters_when_catalog_not_loaded()
    {
        // Empty schema (catalog not loaded) → typed fields are empty, but original params survive a save.
        ParameterSpecProvider noSpecs = _ => [];
        var row = new AutomationStepRowViewModel(noSpecs, AutomationStepType.Retention, "",
            new Dictionary<string, string> { ["retentionDays"] = "30" });

        Assert.Empty(row.Fields);
        Assert.Equal("30", row.ToInput().Parameters!["retentionDays"]);
    }

    [Fact]
    public void EditSelected_loads_triggers_steps_and_policy()
    {
        var vm = NewVm();
        var automation = new AutomationResponse
        {
            Id = Guid.NewGuid(),
            Key = "watch",
            Name = "Watch",
            ScopeKind = AutomationScopeKind.Branch,
            BranchKey = "Alfa",
            Cron = "0 6 * * *",
            EventTriggers = [NotificationEvent.Failed],
            EventDebounceSeconds = 120,
            Steps =
            [
                new AutomationStepResponse { Type = AutomationStepType.Readiness, Name = "Vstupy" },
                new AutomationStepResponse { Type = AutomationStepType.Health },
            ],
            TimeoutSeconds = 300,
            MaxAttempts = 2,
            RetryDelaySeconds = 10,
            FailureThreshold = 5,
            Events = [NotificationEvent.ReadinessFailed, NotificationEvent.AutomationRecovered],
            Enabled = true,
            Version = 3,
        };
        vm.Automations.Add(automation);
        vm.Selected = automation;

        vm.EditSelectedCommand.Execute(null);

        Assert.Equal("watch", vm.Key);
        Assert.Equal("0 6 * * *", vm.Cron);
        Assert.Equal(2, vm.Steps.Count);
        Assert.Equal("Vstupy", vm.Steps[0].Name);
        Assert.True(vm.EventTriggerOptions.Single(o => o.Event == NotificationEvent.Failed).IsChecked);
        Assert.False(vm.EventTriggerOptions.Single(o => o.Event == NotificationEvent.Completed).IsChecked);
        Assert.Equal(120, vm.EventDebounceSeconds);
        Assert.Equal(2, vm.MaxAttempts);
        Assert.Equal(5, vm.FailureThreshold);
        Assert.True(vm.EventReadinessFailed);
        Assert.True(vm.EventAutomationRecovered);
        Assert.False(vm.EventHealthDegraded);
        Assert.Equal(3, vm.EditingVersion);
    }
}
