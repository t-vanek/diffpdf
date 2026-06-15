using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The Automatizace summary strip and the steps editor of the automation definitions view-model.</summary>
public class AutomationDefinitionsViewModelTests
{
    // A FakeApi-backed session so the scope dropdowns' branch/instance loads have something to call (default:
    // empty lists). Tests that exercise the cascade pass their own FakeApi with branches/instances.
    private static AutomationDefinitionsViewModel NewVm(FakeApi? api = null)
    {
        var session = new ServerSession
        {
            Client = new DiffPdfClient(new HttpClient(api ?? new FakeApi
            {
                Branches = Array.Empty<Branch>(),
                InstancesByBranch = _ => Array.Empty<Instance>(),
            })
            { BaseAddress = new Uri("http://localhost") }),
        };
        // The hub is constructed but never connected — Activate (which would join groups) is not exercised here.
        return new(session, new DialogService(new ToastService()), new JobProgressHubClient(session, new TokenSource(session)));
    }

    [Fact]
    public void Summary_counts_automation_health_from_the_list()
    {
        var vm = NewVm();
        vm.Automations.Add(new AutomationResponse { Id = Guid.NewGuid(), Enabled = true, LastOutcome = AutomationRunOutcome.Ok });
        vm.Automations.Add(new AutomationResponse { Id = Guid.NewGuid(), Enabled = true, LastOutcome = AutomationRunOutcome.Failed });
        vm.Automations.Add(new AutomationResponse { Id = Guid.NewGuid(), Enabled = false, LastOutcome = null });

        var byLabel = vm.Summary.ToDictionary(l => l.Label, l => l.Count);
        Assert.Equal(3, byLabel["Celkem"]);
        Assert.Equal(1, byLabel["V pořádku"]);
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
    public void StepRow_carries_type_blanks_name_and_serializes_parameter_fields()
    {
        // The editor renders typed parameter fields from the step catalog (here the two Int fields of
        // „Úklid reportů"); ToInput() blanks a whitespace name and serializes those fields back to key=value.
        IReadOnlyList<AutomationParameterSpecResponse> SpecsFor(AutomationStepType _) =>
        [
            new() { Key = "retentionDays", Label = "Doba uchování", Type = AutomationParameterType.Int },
            new() { Key = "maxPerTick", Label = "Max. na běh", Type = AutomationParameterType.Int },
        ];

        var row = new AutomationStepRowViewModel(
            SpecsFor,
            AutomationStepType.Retention,
            name: "  ",
            parameters: new Dictionary<string, string> { ["retentionDays"] = "30", ["maxPerTick"] = "100" });

        var input = row.ToInput();
        Assert.Equal(AutomationStepType.Retention, input.Type);
        Assert.Null(input.Name);
        Assert.Equal("30", input.Parameters!["retentionDays"]);
        Assert.Equal("100", input.Parameters!["maxPerTick"]);
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
        Assert.Equal(ScheduleKind.Daily, vm.SelectedScheduleKind!.Kind); // "m h * * *" → friendly „Denně“
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

    [Fact]
    public void Scope_dropdowns_reveal_by_scope_and_narrowing_clears_keys()
    {
        var vm = NewVm();
        Assert.False(vm.IsBranchScope);   // Global default — no scope pickers
        Assert.False(vm.IsInstanceScope);

        vm.ScopeKind = AutomationScopeKind.Instance;
        Assert.True(vm.IsBranchScope);    // Instance scope reveals both branch + instance pickers
        Assert.True(vm.IsInstanceScope);

        vm.ScopeKind = AutomationScopeKind.Branch;
        Assert.True(vm.IsBranchScope);
        Assert.False(vm.IsInstanceScope); // narrowing to Branch hides the instance picker

        vm.ScopeKind = AutomationScopeKind.Global;
        Assert.Equal(string.Empty, vm.BranchKey);   // Global drops both keys
        Assert.Equal(string.Empty, vm.InstanceKey);
        Assert.False(vm.IsBranchScope);
    }

    [Fact]
    public void Picking_a_branch_loads_its_instances_into_the_instance_dropdown()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                Branches = new[] { new Branch { Key = "Alfa" }, new Branch { Key = "Beta" } },
                InstancesByBranch = bk => bk == "Alfa"
                    ? new[] { new Instance { Key = "LamaEnergy" }, new Instance { Key = "Centropol" } }
                    : Array.Empty<Instance>(),
            };
            var vm = NewVm(api);

            await VmTest.InvokeAsync(vm, "LoadBranchOptionsAsync");
            Assert.Equal(new[] { "Alfa", "Beta" }, vm.BranchOptions.ToArray());

            // Selecting a branch cascades its instances into the instance dropdown.
            await VmTest.InvokeAsync(vm, "ReloadInstanceOptionsAsync", "Alfa");
            Assert.Equal(new[] { "LamaEnergy", "Centropol" }, vm.InstanceOptions.ToArray());

            // A branch with no instances empties the list.
            await VmTest.InvokeAsync(vm, "ReloadInstanceOptionsAsync", "Beta");
            Assert.Empty(vm.InstanceOptions);
        });
    }

    [Fact]
    public void Cadence_maps_stored_schedule_to_friendly_frequency()
    {
        var vm = NewVm();

        VmTest.Invoke(vm, "LoadCadence", null, 300);
        Assert.Equal(ScheduleKind.Interval, vm.SelectedScheduleKind!.Kind);
        Assert.Equal(300, vm.IntervalSeconds);

        VmTest.Invoke(vm, "LoadCadence", null, null);
        Assert.Equal(ScheduleKind.None, vm.SelectedScheduleKind!.Kind);

        VmTest.Invoke(vm, "LoadCadence", "*/15 * * * *", null); // not a single daily/weekly/monthly time → raw
        Assert.Equal(ScheduleKind.Custom, vm.SelectedScheduleKind!.Kind);
        Assert.Equal("*/15 * * * *", vm.Cron);

        VmTest.Invoke(vm, "LoadCadence", "0 6 * * *", null);    // structure maps to „Denně“ regardless of host TZ
        Assert.Equal(ScheduleKind.Daily, vm.SelectedScheduleKind!.Kind);
    }
}
