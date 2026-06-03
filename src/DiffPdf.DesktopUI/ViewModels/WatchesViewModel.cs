using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Folder-watches: list all + set/get/delete the watch of a chosen instance.</summary>
public partial class WatchesViewModel : PageViewModel
{
    private readonly ServerSession _session;

    public override string Title => "Watches";
    public override int NavOrder => 4;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<Instance> Instances { get; } = [];
    public ObservableCollection<WatchResponse> Watches { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private decimal _stabilitySeconds = 30;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private WatchResponse? _current;
    [ObservableProperty] private string? _info;

    public WatchesViewModel(ServerSession session) => _session = session;

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        await LoadBranchesAsync();
        await LoadAllAsync();
    });

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

    private async Task LoadAllAsync()
    {
        Watches.Clear();
        foreach (var w in await _session.Require().ListWatchesAsync()) Watches.Add(w);
    }

    partial void OnSelectedBranchChanged(Branch? value) => _ = RunAsync(LoadInstancesAsync);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAllAsync);

    [RelayCommand]
    private Task SetAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        Current = await _session.Require().SetWatchAsync(bk, ik,
            new SetWatchRequest { StabilitySeconds = (int)StabilitySeconds, Enabled = Enabled });
        Info = $"Watch nastaven pro {bk}/{ik}.";
        await LoadAllAsync();
    });

    [RelayCommand]
    private Task GetAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        Current = await _session.Require().GetWatchAsync(bk, ik);
        Info = Current is null ? "Watch neexistuje." : $"Watch v{Current.Version}.";
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        await _session.Require().DeleteWatchAsync(bk, ik);
        Current = null;
        Info = $"Watch smazán pro {bk}/{ik}.";
        await LoadAllAsync();
    });

    private void RequireInstance(out string branchKey, out string instanceKey)
    {
        if (SelectedBranch is null || SelectedInstance is null)
            throw new InvalidOperationException("Vyber branch i instanci.");
        branchKey = SelectedBranch.Key;
        instanceKey = SelectedInstance.Key;
    }
}
