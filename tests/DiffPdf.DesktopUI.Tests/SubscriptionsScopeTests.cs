using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Scope dropdowns of <see cref="SubscriptionsViewModel"/>: the instance offer follows the branch selection
/// (never silently dropping a ticked key), and the button summaries compact large selections. Driven on the
/// <see cref="AsyncPump"/> with a real <see cref="DiffPdfClient"/> over <see cref="FakeApi"/>.
/// </summary>
public class SubscriptionsScopeTests
{
    private static Branch Branch(string key) => new() { Id = Guid.NewGuid(), Key = key, Name = key, Enabled = true };

    private static Instance Inst(string key) =>
        new() { Id = Guid.NewGuid(), Key = key, Name = key, BasePath = @"D:\pdfs\" + key, Enabled = true };

    private static SubscriptionsViewModel NewVm() =>
        new(new ServerSession
        {
            Client = new DiffPdfClient(new HttpClient(new FakeApi
            {
                Branches = new[] { Branch("alfa"), Branch("beta") },
                InstancesByBranch = b => b switch
                {
                    "alfa" => new[] { Inst("lama"), Inst("koza") },
                    "beta" => new[] { Inst("ovce") },
                    _ => Array.Empty<Instance>(),
                },
            }) { BaseAddress = new Uri("http://localhost") }),
        }, null!);

    [Fact]
    public void Instance_offer_follows_the_branch_selection()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm();
            await VmTest.InvokeAsync(vm, "LoadScopeChoicesAsync");

            // No branch restriction → the union of every branch's instances.
            Assert.Equal(new[] { "koza", "lama", "ovce" }, vm.InstanceChoices.Select(c => c.Key));

            vm.BranchChoices.Single(c => c.Key == "alfa").IsChecked = true;
            Assert.Equal(new[] { "koza", "lama" }, vm.InstanceChoices.Select(c => c.Key));

            // Lifting the restriction restores the full offer.
            vm.BranchChoices.Single(c => c.Key == "alfa").IsChecked = false;
            Assert.Equal(new[] { "koza", "lama", "ovce" }, vm.InstanceChoices.Select(c => c.Key));
        });
    }

    [Fact]
    public void Narrowing_branches_never_drops_a_ticked_instance()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm();
            await VmTest.InvokeAsync(vm, "LoadScopeChoicesAsync");

            vm.InstanceChoices.Single(c => c.Key == "ovce").IsChecked = true; // beta's instance
            vm.BranchChoices.Single(c => c.Key == "alfa").IsChecked = true;   // then restrict to alfa

            // The ticked key survives the narrowing (still listed AND ticked), alfa's instances are offered.
            Assert.Equal(new[] { "koza", "lama", "ovce" }, vm.InstanceChoices.Select(c => c.Key));
            Assert.True(vm.InstanceChoices.Single(c => c.Key == "ovce").IsChecked);
            Assert.True(vm.HasInstanceSelection);
        });
    }

    [Fact]
    public void Summaries_compact_and_selection_flags_track_the_ticks()
    {
        AsyncPump.Run(async () =>
        {
            var vm = NewVm();
            await VmTest.InvokeAsync(vm, "LoadScopeChoicesAsync");

            Assert.Equal("Všechny větve", vm.BranchScopeSummary);
            Assert.False(vm.HasBranchSelection);

            // 3+ ticks → a count instead of an endless key list (all 3 instances offered while unrestricted).
            foreach (var c in vm.InstanceChoices) c.IsChecked = true;
            Assert.Equal("Vybráno: 3", vm.InstanceScopeSummary);

            vm.BranchChoices.Single(c => c.Key == "alfa").IsChecked = true;
            Assert.Equal("alfa", vm.BranchScopeSummary);
            Assert.True(vm.HasBranchSelection);
        });
    }
}
