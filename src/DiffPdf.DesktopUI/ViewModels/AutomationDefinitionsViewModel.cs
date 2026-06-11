using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>One editovatelný krok pipeline v editoru automatizace.</summary>
public partial class AutomationStepRowViewModel : ObservableObject
{
    public AutomationStepType[] Types { get; } = Enum.GetValues<AutomationStepType>();

    [ObservableProperty] private AutomationStepType _type = AutomationStepType.Readiness;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _parametersText = string.Empty;

    public AutomationStepInput ToInput() => new()
    {
        Type = Type,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name,
        Parameters = ParseParameters(),
    };

    public static AutomationStepRowViewModel From(AutomationStepResponse step) => new()
    {
        Type = step.Type,
        Name = step.Name ?? string.Empty,
        ParametersText = string.Join('\n', step.Parameters.Select(kv => $"{kv.Key}={kv.Value}")),
    };

    private Dictionary<string, string> ParseParameters()
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

/// <summary>Zaškrtávací volba jedné spouštěcí události v editoru automatizace.</summary>
public partial class EventTriggerOptionViewModel(NotificationEvent @event, string label) : ObservableObject
{
    public NotificationEvent Event { get; } = @event;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Jedna kategorie v galerii šablon (nadpis + šablony této kategorie).</summary>
public sealed record AutomationTemplateGroup(string Title, IReadOnlyList<AutomationTemplateResponse> Templates);

/// <summary>
/// Automatizace (definice) — obsah sekce Automatizace: seznam + CRUD, spustit teď a historie běhů
/// (jeden řádek na pokus, s výsledky kroků). Automatizace = spouštěče (cron/interval, události, manuál)
/// + pipeline kroků + politika běhu (timeout, opakování, eskalace). Není to samostatná navigační stránka;
/// hostuje ji <see cref="AutomationsViewModel"/>.
/// </summary>
public partial class AutomationDefinitionsViewModel : ViewModelBase, IAutomationContent
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public AutomationScopeKind[] Scopes { get; } = Enum.GetValues<AutomationScopeKind>();

    public ObservableCollection<AutomationResponse> Automations { get; } = [];
    public ObservableCollection<AutomationRunResponse> Runs { get; } = [];
    public ObservableCollection<AutomationStepRowViewModel> Steps { get; } = [];

    /// <summary>Galerie editovatelných šablon, seskupená do kategorií (Monitorovací → Provozní → Údržbové → Synchronizační).</summary>
    public ObservableCollection<AutomationTemplateGroup> TemplateGroups { get; } = [];

    private static readonly AutomationCategory[] CategoryOrder =
        [AutomationCategory.Monitoring, AutomationCategory.Operations, AutomationCategory.Maintenance, AutomationCategory.Synchronization];

    /// <summary>Spouštěcí události (multi-select) — automatizace se spustí, když nastane zaškrtnutá událost.</summary>
    public IReadOnlyList<EventTriggerOptionViewModel> EventTriggerOptions { get; } =
    [
        new(NotificationEvent.Completed, "Porovnání dokončeno"),
        new(NotificationEvent.CompletedWithErrors, "Dokončeno s chybami"),
        new(NotificationEvent.GateViolated, "Porušená brána"),
        new(NotificationEvent.Failed, "Porovnání selhalo"),
        new(NotificationEvent.ReadinessFailed, "Připravenost selhala"),
        new(NotificationEvent.HealthDegraded, "Zhoršené zdraví serveru"),
        new(NotificationEvent.StructureDrift, "Nesoulad struktury"),
        new(NotificationEvent.JobStalled, "Job se zasekl"),
        new(NotificationEvent.AutomationRecovered, "Automatizace obnovena"),
        new(NotificationEvent.AutomationFailing, "Automatizace opakovaně selhává"),
    ];

    [ObservableProperty] private AutomationResponse? _selected;
    [ObservableProperty] private AutomationRunResponse? _selectedRun;
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private AutomationScopeKind _scopeKind = AutomationScopeKind.Global;
    [ObservableProperty] private string _branchKey = string.Empty;
    [ObservableProperty] private string _instanceKey = string.Empty;
    [ObservableProperty] private string _cron = string.Empty;
    [ObservableProperty] private decimal _intervalSeconds = 300;
    [ObservableProperty] private decimal _eventDebounceSeconds = 60;
    [ObservableProperty] private decimal _timeoutSeconds = 600;
    [ObservableProperty] private decimal _maxAttempts = 1;
    [ObservableProperty] private decimal _retryDelaySeconds = 30;
    [ObservableProperty] private decimal _failureThreshold = 3;
    [ObservableProperty] private bool _eventReadinessFailed = true;
    [ObservableProperty] private bool _eventHealthDegraded = true;
    [ObservableProperty] private bool _eventStructureDrift = true;
    [ObservableProperty] private bool _eventAutomationRecovered;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private long? _editingVersion;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private bool _hasNoAutomations;
    [ObservableProperty] private bool _hasNoRuns;

    public bool IsEditing => EditingVersion is not null;

    /// <summary>At-a-glance automation health, derived from the loaded list (no extra call). Drives the summary strip.</summary>
    public IReadOnlyList<StatLine> Summary =>
    [
        new("Celkem", Automations.Count),
        new("OK", Automations.Count(a => a.LastOutcome == AutomationRunOutcome.Ok)) { Tone = StatTone.Good },
        new("Varování", Automations.Count(a => a.LastOutcome == AutomationRunOutcome.Warning)) { Tone = StatTone.Warning },
        new("Selhané", Automations.Count(a => a.LastOutcome == AutomationRunOutcome.Failed)) { Tone = StatTone.Failed },
        new("Nespuštěné", Automations.Count(a => a.LastOutcome is null)) { Tone = StatTone.Neutral },
        new("Zakázané", Automations.Count(a => !a.Enabled)) { Tone = StatTone.Paused },
    ];

    public AutomationDefinitionsViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        Steps.Add(new AutomationStepRowViewModel());
    }

    public Task ActivateAsync() => RunAsync(async () =>
    {
        await LoadAsync();
        if (TemplateGroups.Count == 0) await LoadTemplatesAsync();
    });

    private async Task LoadAsync()
    {
        Automations.Clear();
        foreach (var a in await _session.Require().ListAutomationsAsync()) Automations.Add(a);
        HasNoAutomations = Automations.Count == 0;
        OnPropertyChanged(nameof(Summary));
    }

    private async Task LoadTemplatesAsync()
    {
        var templates = await _session.Require().ListAutomationTemplatesAsync();
        TemplateGroups.Clear();
        foreach (var category in CategoryOrder)
        {
            var inCategory = templates.Where(t => t.Category == category).ToList();
            if (inCategory.Count > 0)
                TemplateGroups.Add(new AutomationTemplateGroup(AutomationCategoryLabelConverter.Label(category), inCategory));
        }
    }

    partial void OnEditingVersionChanged(long? value) => OnPropertyChanged(nameof(IsEditing));

    partial void OnSelectedChanged(AutomationResponse? value) => _ = RunAsync(LoadRunsAsync);

    private async Task LoadRunsAsync()
    {
        Runs.Clear();
        SelectedRun = null;
        HasNoRuns = false;
        if (Selected is null) return;
        foreach (var r in await _session.Require().ListAutomationRunsAsync(Selected.Id, 50)) Runs.Add(r);
        HasNoRuns = Runs.Count == 0;
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);

    [RelayCommand]
    private void New()
    {
        EditingVersion = null;
        Key = Name = string.Empty;
        ScopeKind = AutomationScopeKind.Global;
        BranchKey = InstanceKey = Cron = string.Empty;
        IntervalSeconds = 300;
        EventDebounceSeconds = 60;
        TimeoutSeconds = 600;
        MaxAttempts = 1;
        RetryDelaySeconds = 30;
        FailureThreshold = 3;
        Steps.Clear();
        Steps.Add(new AutomationStepRowViewModel());
        foreach (var opt in EventTriggerOptions) opt.IsChecked = false;
        EventReadinessFailed = EventHealthDegraded = EventStructureDrift = true;
        EventAutomationRecovered = false;
        Enabled = true;
        Info = null;
    }

    /// <summary>Předvyplní editor ze šablony. Vznikne běžná (nová) automatizace — vše zůstává editovatelné.</summary>
    [RelayCommand]
    private void UseTemplate(AutomationTemplateResponse? template)
    {
        if (template is null) return;

        EditingVersion = null;
        Key = SuggestKey(template.Key);
        Name = template.DisplayName;
        ScopeKind = template.DefaultScope;
        BranchKey = InstanceKey = string.Empty;
        Cron = template.RecommendedCron ?? string.Empty;
        IntervalSeconds = template.RecommendedIntervalSeconds ?? 0;
        EventDebounceSeconds = 60;
        TimeoutSeconds = 600;
        MaxAttempts = 1;
        RetryDelaySeconds = 30;
        FailureThreshold = 3;

        Steps.Clear();
        foreach (var step in template.Steps) Steps.Add(AutomationStepRowViewModel.From(step));
        if (Steps.Count == 0) Steps.Add(new AutomationStepRowViewModel());

        foreach (var opt in EventTriggerOptions) opt.IsChecked = false;
        EventReadinessFailed = template.DefaultEvents.Contains(NotificationEvent.ReadinessFailed);
        EventHealthDegraded = template.DefaultEvents.Contains(NotificationEvent.HealthDegraded);
        EventStructureDrift = template.DefaultEvents.Contains(NotificationEvent.StructureDrift);
        EventAutomationRecovered = template.DefaultEvents.Contains(NotificationEvent.AutomationRecovered);
        Enabled = true;
        Info = $"Předvyplněno ze šablony {template.DisplayName}. Uprav podle potřeby a ulož.";
    }

    /// <summary>Navrhne klíč nekolidující s existujícími automatizacemi (base, base-2, base-3, …).</summary>
    private string SuggestKey(string baseKey)
    {
        if (Automations.All(a => a.Key != baseKey)) return baseKey;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseKey}-{i}";
            if (Automations.All(a => a.Key != candidate)) return candidate;
        }
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is not { } a) return;
        EditingVersion = a.Version;
        Key = a.Key;
        Name = a.Name;
        ScopeKind = a.ScopeKind;
        BranchKey = a.BranchKey ?? string.Empty;
        InstanceKey = a.InstanceKey ?? string.Empty;
        Cron = a.Cron ?? string.Empty;
        IntervalSeconds = a.IntervalSeconds ?? 0;
        EventDebounceSeconds = a.EventDebounceSeconds;
        TimeoutSeconds = a.TimeoutSeconds;
        MaxAttempts = a.MaxAttempts;
        RetryDelaySeconds = a.RetryDelaySeconds;
        FailureThreshold = a.FailureThreshold;
        Steps.Clear();
        foreach (var step in a.Steps) Steps.Add(AutomationStepRowViewModel.From(step));
        if (Steps.Count == 0) Steps.Add(new AutomationStepRowViewModel());
        foreach (var opt in EventTriggerOptions) opt.IsChecked = a.EventTriggers.Contains(opt.Event);
        EventReadinessFailed = a.Events.Contains(NotificationEvent.ReadinessFailed);
        EventHealthDegraded = a.Events.Contains(NotificationEvent.HealthDegraded);
        EventStructureDrift = a.Events.Contains(NotificationEvent.StructureDrift);
        EventAutomationRecovered = a.Events.Contains(NotificationEvent.AutomationRecovered);
        Enabled = a.Enabled;
        Info = $"Editace automatizace {a.Key} (v{a.Version}).";
    }

    [RelayCommand]
    private void AddStep() => Steps.Add(new AutomationStepRowViewModel());

    [RelayCommand]
    private void RemoveStep(AutomationStepRowViewModel row)
    {
        if (Steps.Count > 1) Steps.Remove(row); // pipeline needs at least one step
    }

    [RelayCommand]
    private void MoveStepUp(AutomationStepRowViewModel row)
    {
        int i = Steps.IndexOf(row);
        if (i > 0) Steps.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveStepDown(AutomationStepRowViewModel row)
    {
        int i = Steps.IndexOf(row);
        if (i >= 0 && i < Steps.Count - 1) Steps.Move(i, i + 1);
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var client = _session.Require();
        string? bk = string.IsNullOrWhiteSpace(BranchKey) ? null : BranchKey;
        string? ik = string.IsNullOrWhiteSpace(InstanceKey) ? null : InstanceKey;
        string? cron = string.IsNullOrWhiteSpace(Cron) ? null : Cron;
        int? interval = cron is null && IntervalSeconds > 0 ? (int)IntervalSeconds : null;
        var steps = Steps.Select(s => s.ToInput()).ToList();
        var triggers = EventTriggerOptions.Where(o => o.IsChecked).Select(o => o.Event).ToList();

        if (EditingVersion is { } version && Selected is { } sel)
        {
            await client.UpdateAutomationAsync(sel.Id, new UpdateAutomationRequest
            {
                Key = Key, Name = Name, Steps = steps, Version = version,
                ScopeKind = ScopeKind, BranchKey = bk, InstanceKey = ik,
                Cron = cron, IntervalSeconds = interval,
                EventTriggers = triggers, EventDebounceSeconds = (int)EventDebounceSeconds,
                TimeoutSeconds = (int)TimeoutSeconds, MaxAttempts = (int)MaxAttempts,
                RetryDelaySeconds = (int)RetryDelaySeconds, FailureThreshold = (int)FailureThreshold,
                Events = Events(), Enabled = Enabled,
            });
            Info = "Uloženo (úprava).";
        }
        else
        {
            await client.CreateAutomationAsync(new CreateAutomationRequest
            {
                Key = Key, Name = Name, Steps = steps,
                ScopeKind = ScopeKind, BranchKey = bk, InstanceKey = ik,
                Cron = cron, IntervalSeconds = interval,
                EventTriggers = triggers, EventDebounceSeconds = (int)EventDebounceSeconds,
                TimeoutSeconds = (int)TimeoutSeconds, MaxAttempts = (int)MaxAttempts,
                RetryDelaySeconds = (int)RetryDelaySeconds, FailureThreshold = (int)FailureThreshold,
                Events = Events(), Enabled = Enabled,
            });
            Info = "Vytvořeno.";
        }
        await LoadAsync();
        _dialogs.ShowToast(Info!, ToastKind.Success);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected is not { } a) throw new InvalidOperationException("Vyber automatizaci.");
        if (!await _dialogs.ConfirmAsync("Smazat automatizaci", $"Opravdu smazat automatizaci '{a.Key}'?"))
            return;
        await _session.Require().DeleteAutomationAsync(a.Id);
        Info = "Smazáno.";
        await LoadAsync();
        _dialogs.ShowToast("Automatizace smazána.", ToastKind.Success);
    });

    [RelayCommand]
    private Task RunNowAsync() => RunAsync(async () =>
    {
        if (Selected is not { } a) throw new InvalidOperationException("Vyber automatizaci.");
        var run = await _session.Require().RunAutomationAsync(a.Id);
        Info = $"Automatizace proběhla: {run.Outcome}.";
        _dialogs.ShowToast($"Automatizace proběhla: {run.Outcome}.", ToastKind.Info);
        await LoadRunsAsync();
        await LoadAsync(); // refresh the automation's last outcome + the summary strip
    });

    private List<NotificationEvent> Events()
    {
        var list = new List<NotificationEvent>();
        if (EventReadinessFailed) list.Add(NotificationEvent.ReadinessFailed);
        if (EventHealthDegraded) list.Add(NotificationEvent.HealthDegraded);
        if (EventStructureDrift) list.Add(NotificationEvent.StructureDrift);
        if (EventAutomationRecovered) list.Add(NotificationEvent.AutomationRecovered);
        return list;
    }
}
