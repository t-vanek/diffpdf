using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Schedules under an instance: CRUD (optimistic Version), run-now, and run history.</summary>
public partial class SchedulesViewModel : PageViewModel
{
    private readonly ServerSession _session;

    public override string Title => "Schedules";
    public override int NavOrder => 3;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<Instance> Instances { get; } = [];
    public ObservableCollection<ScheduleResponse> Schedules { get; } = [];
    public ObservableCollection<ScheduleRunResponse> Runs { get; } = [];

    public ComparisonOptionsViewModel Options { get; } = new();
    public BatchGateViewModel Gate { get; } = new();

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private ScheduleResponse? _selectedSchedule;

    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _cron = "0 2 * * *";
    [ObservableProperty] private string _searchPattern = "*.pdf";
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private decimal _maxDegreeOfParallelism;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private long? _editingVersion;
    [ObservableProperty] private string? _info;

    public bool IsEditing => EditingVersion is not null;

    public SchedulesViewModel(ServerSession session) => _session = session;

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

    private async Task LoadSchedulesAsync()
    {
        Schedules.Clear();
        if (SelectedBranch is null || SelectedInstance is null) return;
        foreach (var s in await _session.Require().ListSchedulesAsync(SelectedBranch.Key, SelectedInstance.Key))
            Schedules.Add(s);
    }

    partial void OnSelectedBranchChanged(Branch? value) => _ = RunAsync(LoadInstancesAsync);
    partial void OnSelectedInstanceChanged(Instance? value) => _ = RunAsync(LoadSchedulesAsync);
    partial void OnEditingVersionChanged(long? value) => OnPropertyChanged(nameof(IsEditing));

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadBranchesAsync();
        await LoadInstancesAsync();
        await LoadSchedulesAsync();
    });

    [RelayCommand]
    private void New()
    {
        EditingVersion = null;
        Key = Name = string.Empty;
        Cron = "0 2 * * *";
        SearchPattern = "*.pdf";
        Recursive = Enabled = true;
        MaxDegreeOfParallelism = 0;
        Options.Load(new ComparisonOptions());
        Gate.Load(null);
        Info = null;
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (SelectedSchedule is not { } s) return;
        EditingVersion = s.Version;
        Key = s.Key;
        Name = s.Name;
        Cron = s.Cron;
        SearchPattern = s.SearchPattern;
        Recursive = s.Recursive;
        Enabled = s.Enabled;
        MaxDegreeOfParallelism = s.MaxDegreeOfParallelism;
        Options.Load(s.Options);
        Gate.Load(s.Gate);
        Info = $"Editace rozvrhu '{s.Key}' (v{s.Version}).";
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        var client = _session.Require();

        if (EditingVersion is { } version)
        {
            await client.UpdateScheduleAsync(bk, ik, Key, new UpdateScheduleRequest
            {
                Name = Name,
                Cron = Cron,
                Options = Options.ToOptions(),
                Gate = Gate.ToGate(),
                SearchPattern = SearchPattern,
                Recursive = Recursive,
                MaxDegreeOfParallelism = (int)MaxDegreeOfParallelism,
                Enabled = Enabled,
                Version = version,
            });
            Info = $"Uloženo (update) '{Key}'.";
        }
        else
        {
            await client.CreateScheduleAsync(bk, ik, new CreateScheduleRequest
            {
                Key = Key,
                Name = Name,
                Cron = Cron,
                Options = Options.ToOptions(),
                Gate = Gate.ToGate(),
                SearchPattern = SearchPattern,
                Recursive = Recursive,
                MaxDegreeOfParallelism = (int)MaxDegreeOfParallelism,
                Enabled = Enabled,
            });
            Info = $"Vytvořeno '{Key}'.";
        }

        await LoadSchedulesAsync();
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        if (SelectedSchedule is not { } s) throw new InvalidOperationException("Vyber rozvrh.");
        await _session.Require().DeleteScheduleAsync(bk, ik, s.Key);
        Info = $"Smazáno '{s.Key}'.";
        await LoadSchedulesAsync();
    });

    [RelayCommand]
    private Task RunNowAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        if (SelectedSchedule is not { } s) throw new InvalidOperationException("Vyber rozvrh.");
        var jobId = await _session.Require().RunScheduleNowAsync(bk, ik, s.Key);
        Info = $"Spuštěno teď: job {jobId} (viz Jobs).";
    });

    [RelayCommand]
    private Task LoadRunsAsync() => RunAsync(async () =>
    {
        RequireInstance(out var bk, out var ik);
        if (SelectedSchedule is not { } s) throw new InvalidOperationException("Vyber rozvrh.");
        Runs.Clear();
        foreach (var r in await _session.Require().ListScheduleRunsAsync(bk, ik, s.Key, 50)) Runs.Add(r);
    });

    private void RequireInstance(out string branchKey, out string instanceKey)
    {
        if (SelectedBranch is null || SelectedInstance is null)
            throw new InvalidOperationException("Vyber branch i instanci.");
        branchKey = SelectedBranch.Key;
        instanceKey = SelectedInstance.Key;
    }
}
