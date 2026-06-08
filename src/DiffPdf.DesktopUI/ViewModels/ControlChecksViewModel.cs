using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Automatizované kontroly — jeden typ automatizace. Obsah sekce Automatizace: seznam + CRUD, spustit teď
/// a historie běhů. Není to samostatná navigační stránka; hostuje ji <see cref="AutomationsViewModel"/>.
/// </summary>
public partial class ControlChecksViewModel : ViewModelBase, IAutomationContent
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public CheckType[] Types { get; } = Enum.GetValues<CheckType>();
    public CheckScopeKind[] Scopes { get; } = Enum.GetValues<CheckScopeKind>();

    public ObservableCollection<CheckResponse> Checks { get; } = [];
    public ObservableCollection<CheckRunResponse> Runs { get; } = [];

    [ObservableProperty] private CheckResponse? _selected;
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private CheckType _type = CheckType.Readiness;
    [ObservableProperty] private CheckScopeKind _scopeKind = CheckScopeKind.Global;
    [ObservableProperty] private string _branchKey = string.Empty;
    [ObservableProperty] private string _instanceKey = string.Empty;
    [ObservableProperty] private string _cron = string.Empty;
    [ObservableProperty] private decimal _intervalSeconds = 300;
    [ObservableProperty] private string _parametersText = string.Empty;
    [ObservableProperty] private bool _eventReadinessFailed = true;
    [ObservableProperty] private bool _eventHealthDegraded = true;
    [ObservableProperty] private bool _eventStructureDrift = true;
    [ObservableProperty] private bool _eventCheckRecovered;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private long? _editingVersion;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private bool _hasNoChecks;
    [ObservableProperty] private bool _hasNoRuns;

    public bool IsEditing => EditingVersion is not null;

    /// <summary>At-a-glance automation health, derived from the loaded checks (no extra call). Drives the summary strip.</summary>
    public IReadOnlyList<StatLine> Summary =>
    [
        new("Celkem", Checks.Count),
        new("OK", Checks.Count(c => c.LastOutcome == CheckRunOutcome.Ok)) { Tone = StatTone.Good },
        new("Varování", Checks.Count(c => c.LastOutcome == CheckRunOutcome.Warning)) { Tone = StatTone.Warning },
        new("Selhané", Checks.Count(c => c.LastOutcome == CheckRunOutcome.Failed)) { Tone = StatTone.Failed },
        new("Nespuštěné", Checks.Count(c => c.LastOutcome is null)) { Tone = StatTone.Neutral },
        new("Zakázané", Checks.Count(c => !c.Enabled)) { Tone = StatTone.Paused },
    ];

    public ControlChecksViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public Task ActivateAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        Checks.Clear();
        foreach (var c in await _session.Require().ListChecksAsync()) Checks.Add(c);
        HasNoChecks = Checks.Count == 0;
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnEditingVersionChanged(long? value) => OnPropertyChanged(nameof(IsEditing));

    partial void OnSelectedChanged(CheckResponse? value) => _ = RunAsync(LoadRunsAsync);

    private async Task LoadRunsAsync()
    {
        Runs.Clear();
        HasNoRuns = false;
        if (Selected is null) return;
        foreach (var r in await _session.Require().ListCheckRunsAsync(Selected.Id, 50)) Runs.Add(r);
        HasNoRuns = Runs.Count == 0;
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);

    [RelayCommand]
    private void New()
    {
        EditingVersion = null;
        Key = Name = string.Empty;
        Type = CheckType.Readiness;
        ScopeKind = CheckScopeKind.Global;
        BranchKey = InstanceKey = Cron = string.Empty;
        IntervalSeconds = 300;
        ParametersText = string.Empty;
        EventReadinessFailed = EventHealthDegraded = EventStructureDrift = true;
        EventCheckRecovered = false;
        Enabled = true;
        Info = null;
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is not { } c) return;
        EditingVersion = c.Version;
        Key = c.Key;
        Name = c.Name;
        Type = c.Type;
        ScopeKind = c.ScopeKind;
        BranchKey = c.BranchKey ?? string.Empty;
        InstanceKey = c.InstanceKey ?? string.Empty;
        Cron = c.Cron ?? string.Empty;
        IntervalSeconds = c.IntervalSeconds ?? 0;
        ParametersText = string.Join('\n', c.Parameters.Select(kv => $"{kv.Key}={kv.Value}"));
        EventReadinessFailed = c.Events.Contains(NotificationEvent.ReadinessFailed);
        EventHealthDegraded = c.Events.Contains(NotificationEvent.HealthDegraded);
        EventStructureDrift = c.Events.Contains(NotificationEvent.StructureDrift);
        EventCheckRecovered = c.Events.Contains(NotificationEvent.CheckRecovered);
        Enabled = c.Enabled;
        Info = $"Editace kontroly {c.Key} (v{c.Version}).";
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var client = _session.Require();
        string? bk = string.IsNullOrWhiteSpace(BranchKey) ? null : BranchKey;
        string? ik = string.IsNullOrWhiteSpace(InstanceKey) ? null : InstanceKey;
        string? cron = string.IsNullOrWhiteSpace(Cron) ? null : Cron;
        int? interval = string.IsNullOrWhiteSpace(Cron) ? (int)IntervalSeconds : null;

        if (EditingVersion is { } version && Selected is { } sel)
        {
            await client.UpdateCheckAsync(sel.Id, new UpdateCheckRequest
            {
                Key = Key, Name = Name, Type = Type, Version = version,
                ScopeKind = ScopeKind, BranchKey = bk, InstanceKey = ik,
                Cron = cron, IntervalSeconds = interval,
                Parameters = ParseParameters(), Events = Events(), Enabled = Enabled,
            });
            Info = "Uloženo (úprava).";
        }
        else
        {
            await client.CreateCheckAsync(new CreateCheckRequest
            {
                Key = Key, Name = Name, Type = Type,
                ScopeKind = ScopeKind, BranchKey = bk, InstanceKey = ik,
                Cron = cron, IntervalSeconds = interval,
                Parameters = ParseParameters(), Events = Events(), Enabled = Enabled,
            });
            Info = "Vytvořeno.";
        }
        await LoadAsync();
        _dialogs.ShowToast(Info!, ToastKind.Success);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected is not { } c) throw new InvalidOperationException("Vyber kontrolu.");
        if (!await _dialogs.ConfirmAsync("Smazat kontrolu", $"Opravdu smazat kontrolu '{c.Key}'?"))
            return;
        await _session.Require().DeleteCheckAsync(c.Id);
        Info = "Smazáno.";
        await LoadAsync();
        _dialogs.ShowToast("Kontrola smazána.", ToastKind.Success);
    });

    [RelayCommand]
    private Task RunNowAsync() => RunAsync(async () =>
    {
        if (Selected is not { } c) throw new InvalidOperationException("Vyber kontrolu.");
        var run = await _session.Require().RunCheckAsync(c.Id);
        Info = $"Kontrola proběhla: {run.Outcome}.";
        _dialogs.ShowToast($"Kontrola proběhla: {run.Outcome}.", ToastKind.Info);
        await LoadRunsAsync();
        await LoadAsync(); // refresh the check's last outcome + the summary strip
    });

    private IReadOnlyList<NotificationEvent> Events()
    {
        var list = new List<NotificationEvent>();
        if (EventReadinessFailed) list.Add(NotificationEvent.ReadinessFailed);
        if (EventHealthDegraded) list.Add(NotificationEvent.HealthDegraded);
        if (EventStructureDrift) list.Add(NotificationEvent.StructureDrift);
        if (EventCheckRecovered) list.Add(NotificationEvent.CheckRecovered);
        return list;
    }

    private IReadOnlyDictionary<string, string> ParseParameters()
    {
        var dict = new Dictionary<string, string>();
        foreach (var line in ParametersText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict;
    }
}
