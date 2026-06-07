using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Regression tests for the async re-entrancy bugs in <see cref="InstancesViewModel"/>'s detail loaders:
/// (1) overlapping instance loads must not duplicate rows (the "every instance listed twice" bug), and
/// (2) a selection change while a load is in flight must neither apply stale data nor throw a
/// NullReferenceException. Both are driven by snapshotting the selection and rebuilding atomically after the
/// fetch — which only works because Avalonia marshals async continuations onto the single UI thread, modelled
/// here by <see cref="AsyncPump"/>. A real <see cref="DiffPdfClient"/> runs over the gated <see cref="FakeApi"/>.
/// </summary>
public class InstancesViewModelTests
{
    private static readonly Guid AlfaId = Guid.NewGuid();

    private static Branch Alfa => new() { Id = AlfaId, Key = "alfa", Name = "alfa", Enabled = true };
    private static Branch Beta => new() { Id = Guid.NewGuid(), Key = "beta", Name = "beta", Enabled = true };

    private static Instance Inst(string key) => new()
    {
        Id = Guid.NewGuid(), BranchId = AlfaId, Key = key, Name = key, BasePath = $@"D:\diffpdf\alfa\{key}", Enabled = true,
    };

    // The three instances from the screenshot, in the order the server returns them.
    private static readonly Instance[] AlfaInstances = [Inst("Centropol"), Inst("LamaEnergy"), Inst("Pragoplyn")];

    [Fact]
    public void Overlapping_loads_for_the_same_branch_do_not_duplicate_rows()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi { InstancesByBranch = _ => AlfaInstances, GatedSuffix = "/instances" };
            var vm = NewVm(api);
            VmTest.SetField(vm, "_selectedBranch", Alfa); // set state without firing the auto-load

            // Two loads for the SAME branch, both suspended at the (gated) fetch — exactly what a branch
            // re-select / refresh / page re-activate triggers. Releasing then lets both finish.
            var first = VmTest.InvokeAsync(vm, "LoadInstancesAsync");
            var second = VmTest.InvokeAsync(vm, "LoadInstancesAsync");
            api.Release();
            await Task.WhenAll(first, second);

            // The buggy loader (Clear at top, Add after the await) left [C,L,P,C,L,P]; the fix rebuilds
            // atomically after the fetch, so each instance appears exactly once.
            Assert.Equal(new[] { "Centropol", "LamaEnergy", "Pragoplyn" },
                vm.Instances.Select(r => r.Instance.Key).ToArray());
        });
    }

    [Fact]
    public void Load_for_a_previous_branch_is_discarded_after_the_selection_changed()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                InstancesByBranch = key => key == "alfa" ? AlfaInstances : [],
                GatedSuffix = "/instances",
            };
            var vm = NewVm(api);
            VmTest.SetField(vm, "_selectedBranch", Alfa);

            var load = VmTest.InvokeAsync(vm, "LoadInstancesAsync"); // alfa load, suspended at the fetch
            VmTest.SetField(vm, "_selectedBranch", Beta);            // user switched branch while alfa was loading
            api.Release();
            await load;

            // alfa's rows must NOT be painted under the beta selection (the staleness guard discards them).
            Assert.Empty(vm.Instances);
        });
    }

    [Fact]
    public void Clearing_the_selection_during_a_detail_load_does_not_throw_and_discards_stale_results()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                Readiness = new InstanceReadiness(),
                Stats = new ScopeStatsResponse(),
                Triggers = Array.Empty<TriggerResponse>(),
                GatedSuffix = "/readiness",
            };
            var vm = NewVm(api);
            VmTest.SetField(vm, "_selectedBranch", Alfa);
            VmTest.SetField(vm, "_selectedRow", new InstanceRowViewModel(Inst("Centropol")));

            var load = VmTest.InvokeAsync(vm, "LoadInstanceDetailsAsync"); // suspended at the (gated) readiness fetch
            VmTest.SetField(vm, "_selectedRow", null);                     // user clicked away mid-load — the NRE trigger
            api.Release();

            await load;                 // must NOT throw NullReferenceException (the original bug)
            Assert.Null(vm.Readiness);  // stale result discarded — the selection is gone
            Assert.Empty(vm.Stats);
        });
    }

    [Fact]
    public void Reloading_instances_reconciles_in_place_preserving_selection_and_checkboxes()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm(new FakeApi { InstancesByBranch = _ => AlfaInstances });
            VmTest.SetField(vm, "_selectedBranch", Alfa);

            await VmTest.InvokeAsync(vm, "LoadInstancesAsync");
            Assert.Equal(3, vm.Instances.Count);

            var lama = vm.Instances[1];
            vm.SelectedRow = lama;             // user selects a row (detail panel)
            vm.Instances[0].IsSelected = true; // user checks a row for bulk delete
            Assert.Equal(1, vm.SelectedDeleteCount);

            // Auto-refresh tick (same branch, same data) — reconciles in place.
            await VmTest.InvokeAsync(vm, "LoadInstancesAsync");

            Assert.Equal(3, vm.Instances.Count);     // reconciled, not rebuilt → no duplication
            Assert.Same(lama, vm.SelectedRow);       // selection preserved (same row object)
            Assert.True(vm.Instances[0].IsSelected); // checkbox state preserved
            Assert.Equal(1, vm.SelectedDeleteCount);
        });
    }

    [Fact]
    public void Search_filters_the_view_and_select_all_applies_to_visible_rows()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm(new FakeApi { InstancesByBranch = _ => AlfaInstances });
            VmTest.SetField(vm, "_selectedBranch", Alfa);
            await VmTest.InvokeAsync(vm, "LoadInstancesAsync");
            Assert.Equal(3, vm.InstancesView.Count);

            vm.SearchText = "lama"; // case-insensitive, matches "LamaEnergy" only
            Assert.Equal(1, vm.InstancesView.Count);
            Assert.Equal("LamaEnergy", vm.InstancesView.Cast<InstanceRowViewModel>().Single().Instance.Key);

            // Select-all toggles only the visible (filtered) rows.
            vm.AllSelected = true;
            Assert.True(vm.Instances.Single(r => r.Instance.Key == "LamaEnergy").IsSelected);
            Assert.False(vm.Instances.Single(r => r.Instance.Key == "Centropol").IsSelected);
            Assert.Equal(1, vm.SelectedDeleteCount);
            Assert.True(vm.AllSelected);

            vm.SearchText = "";
            Assert.Equal(3, vm.InstancesView.Count);
            Assert.False(vm.AllSelected);
        });
    }

    [Fact]
    public void Reloading_branches_reconciles_the_picker_preserving_selection()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                Branches = new[] { Alfa, Beta },
                InstancesByBranch = _ => Array.Empty<Instance>(), // selecting a branch loads (empty) instances
            };
            var vm = NewVm(api);

            await VmTest.InvokeAsync(vm, "LoadBranchesAsync");
            Assert.Equal(new[] { "alfa", "beta" }, vm.Branches.Select(b => b.Key).ToArray());

            vm.SelectedBranch = vm.Branches.Single(b => b.Key == "beta"); // user picks beta
            var betaObj = vm.SelectedBranch;

            // A realtime branch event: alfa removed, gamma added; beta stays.
            api.Branches = new[] { Beta, new Branch { Id = Guid.NewGuid(), Key = "gamma", Name = "gamma", Enabled = true } };
            await VmTest.InvokeAsync(vm, "LoadBranchesAsync");

            // beta kept as the SAME object → selection preserved; picker reflects the new membership.
            Assert.Same(betaObj, vm.SelectedBranch);
            Assert.Equal(new[] { "beta", "gamma" }, vm.Branches.Select(b => b.Key).ToArray());
        });
    }

    // Only ServerSession needs to be real; the loaders never touch the dialog service and reach the SignalR
    // hub only inside a try/catch, so null collaborators are safe here.
    private static InstancesViewModel NewVm(HttpMessageHandler handler) =>
        new(new ServerSession { Client = new DiffPdfClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }) },
            null!, null!);
}
