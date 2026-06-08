using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The Automatizace summary strip: automation health counted from the loaded control checks.</summary>
public class ControlChecksViewModelTests
{
    [Fact]
    public void Summary_counts_check_health_from_the_list()
    {
        var vm = new ControlChecksViewModel(new ServerSession(), new DialogService(new ToastService()));
        vm.Checks.Add(new CheckResponse { Id = Guid.NewGuid(), Enabled = true, LastOutcome = CheckRunOutcome.Ok });
        vm.Checks.Add(new CheckResponse { Id = Guid.NewGuid(), Enabled = true, LastOutcome = CheckRunOutcome.Failed });
        vm.Checks.Add(new CheckResponse { Id = Guid.NewGuid(), Enabled = false, LastOutcome = null });

        var byLabel = vm.Summary.ToDictionary(l => l.Label, l => l.Count);
        Assert.Equal(3, byLabel["Celkem"]);
        Assert.Equal(1, byLabel["OK"]);
        Assert.Equal(1, byLabel["Selhané"]);
        Assert.Equal(1, byLabel["Nespuštěné"]);
        Assert.Equal(1, byLabel["Zakázané"]);
        Assert.Equal(0, byLabel["Varování"]);
    }
}
