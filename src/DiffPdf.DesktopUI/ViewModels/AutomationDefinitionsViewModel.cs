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

    /// <summary>Klíče dostupných větví / instancí pro výběr rozsahu (místo ručního psaní); instance kaskádují z větve.</summary>
    public ObservableCollection<string> BranchOptions { get; } = [];
    public ObservableCollection<string> InstanceOptions { get; } = [];

    /// <summary>Frekvence plánu (uživatelsky přívětivá alternativa k psaní cronu) a dny v týdnu pro týdenní plán.</summary>
    public IReadOnlyList<ScheduleKindOption> ScheduleKinds { get; } =
    [
        new(ScheduleKind.None, "Bez časového plánu"),
        new(ScheduleKind.Interval, "Opakovat po intervalu"),
        new(ScheduleKind.Daily, "Denně"),
        new(ScheduleKind.Weekly, "Týdně"),
        new(ScheduleKind.Monthly, "Měsíčně"),
        new(ScheduleKind.Custom, "Vlastní (cron)"),
    ];

    public IReadOnlyList<WeekdayOption> Weekdays { get; } =
    [
        new(DayOfWeek.Monday, "Pondělí"), new(DayOfWeek.Tuesday, "Úterý"), new(DayOfWeek.Wednesday, "Středa"),
        new(DayOfWeek.Thursday, "Čtvrtek"), new(DayOfWeek.Friday, "Pátek"), new(DayOfWeek.Saturday, "Sobota"),
        new(DayOfWeek.Sunday, "Neděle"),
    ];

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranchScope), nameof(IsInstanceScope))]
    private AutomationScopeKind _scopeKind = AutomationScopeKind.Global;

    [ObservableProperty] private string _branchKey = string.Empty;
    [ObservableProperty] private string _instanceKey = string.Empty;
    [ObservableProperty] private string _cron = string.Empty;
    [ObservableProperty] private decimal _intervalSeconds = 300;

    // Schedule builder: a frequency + a local time of day (and weekday / day-of-month), translated to/from the
    // server's UTC cron by CronSchedule. Visibility of the fields below is driven off the chosen frequency.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInterval), nameof(ShowTimeOfDay), nameof(ShowWeekday),
        nameof(ShowDayOfMonth), nameof(ShowCustomCron))]
    private ScheduleKindOption? _selectedScheduleKind;
    [ObservableProperty] private TimeSpan? _scheduleTimeOfDay = new TimeSpan(6, 0, 0);
    [ObservableProperty] private WeekdayOption? _selectedWeekday;
    [ObservableProperty] private decimal _scheduleDayOfMonth = 1;
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

    /// <summary>Rozsah Větev/Instance odkrývá výběr větve; rozsah Instance navíc výběr instance.</summary>
    public bool IsBranchScope => ScopeKind is AutomationScopeKind.Branch or AutomationScopeKind.Instance;
    public bool IsInstanceScope => ScopeKind is AutomationScopeKind.Instance;

    // Which schedule fields the chosen frequency reveals.
    public bool ShowInterval => SelectedScheduleKind?.Kind == ScheduleKind.Interval;
    public bool ShowTimeOfDay => SelectedScheduleKind?.Kind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly;
    public bool ShowWeekday => SelectedScheduleKind?.Kind == ScheduleKind.Weekly;
    public bool ShowDayOfMonth => SelectedScheduleKind?.Kind == ScheduleKind.Monthly;
    public bool ShowCustomCron => SelectedScheduleKind?.Kind == ScheduleKind.Custom;

    /// <summary>At-a-glance automation health, derived from the loaded list (no extra call). Drives the summary strip.</summary>
    public IReadOnlyList<StatLine> Summary =>
    [
        new("Celkem", Automations.Count),
        new("V pořádku", Automations.Count(a => a.LastOutcome == AutomationRunOutcome.Ok)) { Tone = StatTone.Good },
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
        SelectedScheduleKind = ScheduleKinds[0];   // "Bez časového plánu" until a template / edit sets the cadence
        SelectedWeekday = Weekdays[0];              // Pondělí
    }

    public Task ActivateAsync() => RunAsync(async () =>
    {
        await LoadBranchOptionsAsync();
        await LoadAsync();
        if (TemplateGroups.Count == 0) await LoadTemplatesAsync();
    });

    // Branch keys for the scope dropdown — loaded once (and on F5 refresh); selecting a branch cascades its instances.
    private async Task LoadBranchOptionsAsync()
    {
        var keepBranch = BranchKey;
        BranchOptions.Clear();
        foreach (var b in await _session.Require().ListBranchesAsync()) BranchOptions.Add(b.Key);
        BranchKey = BranchOptions.Contains(keepBranch) ? keepBranch : string.Empty;
    }

    partial void OnBranchKeyChanged(string value) => _ = RunAsync(() => ReloadInstanceOptionsAsync(value));

    // Cascade: a branch selection repopulates the instance dropdown, preserving the current instance if it survives.
    private async Task ReloadInstanceOptionsAsync(string? branchKey)
    {
        var keepInstance = InstanceKey;
        InstanceOptions.Clear();
        if (!string.IsNullOrWhiteSpace(branchKey))
            foreach (var i in await _session.Require().ListInstancesAsync(branchKey)) InstanceOptions.Add(i.Key);
        InstanceKey = InstanceOptions.Contains(keepInstance) ? keepInstance : string.Empty;
    }

    // Narrowing the scope drops the now-irrelevant keys (Global → no scope; Branch → no instance).
    partial void OnScopeKindChanged(AutomationScopeKind value)
    {
        if (value == AutomationScopeKind.Global) { BranchKey = string.Empty; InstanceKey = string.Empty; }
        else if (value == AutomationScopeKind.Branch) InstanceKey = string.Empty;
    }

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
    private Task RefreshAsync() => RunAsync(async () => { await LoadBranchOptionsAsync(); await LoadAsync(); });

    [RelayCommand]
    private void New()
    {
        EditingVersion = null;
        Key = Name = string.Empty;
        ScopeKind = AutomationScopeKind.Global;
        BranchKey = InstanceKey = Cron = string.Empty;
        LoadCadence(null, 300); // default: opakovat po 5 minutách (jako dosud), uživatel snadno změní
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
        LoadCadence(template.RecommendedCron, template.RecommendedIntervalSeconds);
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
        LoadCadence(a.Cron, a.IntervalSeconds);
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
        var (cron, interval) = BuildCadence();
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
        if (!await _dialogs.ConfirmAsync("Smazat automatizaci",
                $"Automatizace {UiText.Quote(a.Key)} se smaže a její rozvrh se přestane spouštět.",
                confirmText: "Smazat", danger: true))
            return;
        await _session.Require().DeleteAutomationAsync(a.Id);
        Info = "Automatizace smazána.";
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

    /// <summary>Translates the friendly schedule fields into the (cron, intervalSeconds) the request carries.
    /// Cron wins server-side, so only one is ever non-null. Times are local; CronSchedule emits a UTC cron.</summary>
    private (string? Cron, int? Interval) BuildCadence()
    {
        var tz = TimeZoneInfo.Local;
        var today = DateTime.Today;
        var time = ScheduleTimeOfDay ?? new TimeSpan(6, 0, 0);
        return SelectedScheduleKind?.Kind switch
        {
            ScheduleKind.Interval => (null, IntervalSeconds > 0 ? (int)IntervalSeconds : null),
            ScheduleKind.Daily => (CronSchedule.Daily(time, tz, today), null),
            ScheduleKind.Weekly => (CronSchedule.Weekly(SelectedWeekday?.Day ?? DayOfWeek.Monday, time, tz, today), null),
            ScheduleKind.Monthly => (CronSchedule.Monthly((int)ScheduleDayOfMonth, time, tz, today), null),
            ScheduleKind.Custom => (string.IsNullOrWhiteSpace(Cron) ? null : Cron.Trim(), null),
            _ => (null, null), // None — událostně/ručně
        };
    }

    /// <summary>Sets the schedule frequency + fields from a stored (cron, intervalSeconds). A cron that maps to a
    /// daily/weekly/monthly pattern fills the friendly fields; anything else falls back to "Vlastní (cron)".</summary>
    private void LoadCadence(string? cron, int? intervalSeconds)
    {
        var tz = TimeZoneInfo.Local;
        var today = DateTime.Today;

        if (!string.IsNullOrWhiteSpace(cron)
            && CronSchedule.TryParse(cron, tz, today, out var kind, out var time, out var day, out var dayOfMonth))
        {
            SelectedScheduleKind = ScheduleKinds.First(o => o.Kind == kind);
            ScheduleTimeOfDay = time;
            if (kind == ScheduleKind.Weekly) SelectedWeekday = Weekdays.First(w => w.Day == day);
            if (kind == ScheduleKind.Monthly) ScheduleDayOfMonth = dayOfMonth;
        }
        else if (!string.IsNullOrWhiteSpace(cron))
        {
            SelectedScheduleKind = ScheduleKinds.First(o => o.Kind == ScheduleKind.Custom);
            Cron = cron;
        }
        else if (intervalSeconds is > 0)
        {
            SelectedScheduleKind = ScheduleKinds.First(o => o.Kind == ScheduleKind.Interval);
            IntervalSeconds = intervalSeconds.Value;
        }
        else
        {
            SelectedScheduleKind = ScheduleKinds.First(o => o.Kind == ScheduleKind.None);
        }
    }

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
