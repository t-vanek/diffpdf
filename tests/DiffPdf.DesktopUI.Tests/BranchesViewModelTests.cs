using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Regression tests for <see cref="BranchesViewModel"/>'s periodic auto-refresh: it reconciles the branch
/// list in place rather than clearing and rebuilding, so a refresh must (a) not duplicate rows and (b) keep
/// the user's row selection and per-row checkbox state. Driven on a single-threaded <see cref="AsyncPump"/>
/// (the UI-thread model) with a real <see cref="DiffPdfClient"/> over <see cref="FakeApi"/>.
/// </summary>
public class BranchesViewModelTests
{
    private static Branch Branch(string key) => new() { Id = Guid.NewGuid(), Key = key, Name = key, Enabled = true };
    private static BranchSummary Summary(string key, int instanceCount = 0) =>
        new(Branch(key), instanceCount, new BranchQueueState { BranchKey = key });

    [Fact]
    public void Reloading_reconciles_in_place_preserving_selection_and_checkboxes_without_duplicating()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm(new FakeApi { BranchSummaries = new[] { Summary("alfa"), Summary("beta", instanceCount: 2), Summary("gamma") } });

            Assert.True(vm.HasNoBranches); // empty before the first load

            await VmTest.InvokeAsync(vm, "LoadAsync");
            Assert.Equal(3, vm.Branches.Count);
            Assert.False(vm.HasNoBranches);
            Assert.Equal(2, vm.Branches.Single(r => r.Branch.Key == "beta").InstanceCount); // count came from the summary

            var beta = vm.Branches[1];
            vm.SelectedRow = beta;             // user selects a row (detail panel)
            vm.Branches[0].IsSelected = true;  // user checks a row for bulk delete
            Assert.Equal(1, vm.SelectedDeleteCount);

            // Auto-refresh tick (same data) — reconciles in place.
            await VmTest.InvokeAsync(vm, "LoadAsync");

            Assert.Equal(3, vm.Branches.Count);     // reconciled, not rebuilt → no duplication
            Assert.Same(beta, vm.SelectedRow);      // selection preserved (same row object)
            Assert.True(vm.Branches[0].IsSelected); // checkbox state preserved
            Assert.Equal(1, vm.SelectedDeleteCount);
        });
    }

    [Fact]
    public void Search_filters_the_view_and_select_all_applies_to_visible_rows()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm(new FakeApi { BranchSummaries = new[] { Summary("alfa"), Summary("beta"), Summary("gamma") } });
            await VmTest.InvokeAsync(vm, "LoadAsync");
            Assert.Equal(3, vm.BranchesView.Count);

            vm.SearchText = "ET"; // case-insensitive, matches "beta" only
            Assert.Equal(1, vm.BranchesView.Count);
            Assert.Equal("beta", vm.BranchesView.Cast<BranchRowViewModel>().Single().Branch.Key);

            // Select-all toggles only the visible (filtered) rows.
            vm.AllSelected = true;
            Assert.True(vm.Branches.Single(r => r.Branch.Key == "beta").IsSelected);
            Assert.False(vm.Branches.Single(r => r.Branch.Key == "alfa").IsSelected);
            Assert.Equal(1, vm.SelectedDeleteCount);
            Assert.True(vm.AllSelected);

            // Clearing the filter reveals all rows again; they are not all selected now.
            vm.SearchText = "";
            Assert.Equal(3, vm.BranchesView.Count);
            Assert.False(vm.AllSelected);
        });
    }

    [Fact]
    public void Reloading_adds_new_branches_and_drops_removed_ones()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi { BranchSummaries = new[] { Summary("alfa"), Summary("beta") } };
            var vm = NewVm(api);

            await VmTest.InvokeAsync(vm, "LoadAsync");
            Assert.Equal(new[] { "alfa", "beta" }, vm.Branches.Select(r => r.Branch.Key).ToArray());

            // Server state changes: beta removed, gamma added.
            api.BranchSummaries = new[] { Summary("alfa"), Summary("gamma") };
            await VmTest.InvokeAsync(vm, "LoadAsync");

            Assert.Equal(new[] { "alfa", "gamma" }, vm.Branches.Select(r => r.Branch.Key).ToArray());
        });
    }

    // Only ServerSession is real; LoadAsync/reconcile never touch the dialog service, and reach the SignalR
    // hub only inside a try/catch — so null collaborators are safe here.
    private static BranchesViewModel NewVm(HttpMessageHandler handler) =>
        new(new ServerSession { Client = new DiffPdfClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }) },
            null!, null!);
}
