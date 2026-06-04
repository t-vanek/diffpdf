using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>On-demand triggers: run one instance now, or fan-out across a branch. Jumps to Jobs on launch.</summary>
public partial class TriggersViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly NavigationService _navigation;

    public override string Title => "Spustit porovnání";
    public override int NavOrder => 5;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<Instance> Instances { get; } = [];
    public ObservableCollection<InstanceRunResult> RunResults { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private string? _info;

    public TriggersViewModel(ServerSession session, NavigationService navigation)
    {
        _session = session;
        _navigation = navigation;
    }

    public override Task ActivateAsync() => RunAsync(LoadBranchesAsync);

    private async Task LoadBranchesAsync()
    {
        var list = await _session.Require().ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(b);
    }

    private async Task LoadInstancesAsync()
    {
        Instances.Clear();
        if (SelectedBranch is null) return;
        foreach (var i in await _session.Require().ListInstancesAsync(SelectedBranch.Key)) Instances.Add(i);
    }

    partial void OnSelectedBranchChanged(Branch? value) => _ = RunAsync(LoadInstancesAsync);

    [RelayCommand]
    private Task TriggerInstanceAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null || SelectedInstance is null)
            throw new InvalidOperationException("Vyber větev i instanci.");
        var result = await _session.Require().TriggerBatchAsync(SelectedBranch.Key, SelectedInstance.Key);
        Info = $"Výsledek: {result.Outcome}{(result.JobId is { } id ? $" (úloha {id})" : "")} {result.Detail}";
        if (result.JobId is { } jobId)
            _navigation.GoTo<JobsViewModel>(j => j.OpenJob(jobId));
    });

    [RelayCommand]
    private Task RunBranchAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null) throw new InvalidOperationException("Vyber větev.");
        var result = await _session.Require().RunBranchAsync(SelectedBranch.Key);
        Info = $"Větev '{result.BranchKey}': spuštěno {result.Launched}, přeskočeno {result.Skipped}.";
        RunResults.Clear();
        foreach (var r in result.Instances) RunResults.Add(r);
    });
}
