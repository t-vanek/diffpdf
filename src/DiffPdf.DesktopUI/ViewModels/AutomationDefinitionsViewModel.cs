using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Jedno typované pole parametru kroku, vygenerované z katalogu (popisek, nápověda, meze, výchozí hodnota).
/// Nahrazuje ruční psaní „klíč=hodnota" — uživatel vyplňuje formulář, ne konfiguraci.
/// </summary>
public partial class StepParameterFieldViewModel : ObservableObject
{
    public AutomationParameterSpecResponse Spec { get; }

    [ObservableProperty] private string _value = string.Empty;   // String / Enum
    [ObservableProperty] private bool _boolValue;                 // Bool
    [ObservableProperty] private decimal? _numberValue;           // Int

    public StepParameterFieldViewModel(AutomationParameterSpecResponse spec, string? current)
    {
        Spec = spec;
        string effective = current ?? spec.Default ?? string.Empty;
        switch (spec.Type)
        {
            case AutomationParameterType.Bool:
                _boolValue = bool.TryParse(effective, out var b) && b;
                break;
            case AutomationParameterType.Int:
                _numberValue = decimal.TryParse(effective, out var n) ? n : null;
                break;
            default:
                _value = effective;
                break;
        }
    }

    public bool IsBool => Spec.Type == AutomationParameterType.Bool;
    public bool IsInt => Spec.Type == AutomationParameterType.Int;
    public bool IsEnum => Spec.Type == AutomationParameterType.Enum;
    public bool IsText => Spec.Type == AutomationParameterType.String;

    public string Label => Spec.Label;
    public string? Help => string.IsNullOrWhiteSpace(Spec.Help) ? null : Spec.Help;
    public IReadOnlyList<string> EnumValues => Spec.EnumValues ?? [];
    public decimal Min => Spec.Min ?? 0;
    public decimal Max => Spec.Max ?? decimal.MaxValue;
    public string Watermark => string.IsNullOrWhiteSpace(Spec.Default) ? "" : $"výchozí: {Spec.Default}";

    /// <summary>Hodnota pro uložení; null = nevyplněno (server použije výchozí).</summary>
    public string? Serialized => Spec.Type switch
    {
        AutomationParameterType.Bool => BoolValue ? "true" : "false",
        AutomationParameterType.Int => NumberValue?.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        _ => string.IsNullOrWhiteSpace(Value) ? null : Value.Trim(),
    };
}

/// <summary>One editovatelný krok pipeline v editoru automatizace. Pole parametrů se generují z katalogu
/// kroků (typ, popisek, meze); klíče, které katalog nezná, se beze změny zachovají (round-trip).</summary>
public partial class AutomationStepRowViewModel : ObservableObject
{
    private readonly Func<AutomationStepType, IReadOnlyList<AutomationParameterSpecResponse>> _specsFor;
    private Dictionary<string, string> _unknownParameters = [];

    public AutomationStepType[] Types { get; } = Enum.GetValues<AutomationStepType>();

    [ObservableProperty] private AutomationStepType _type;
    [ObservableProperty] private string _name = string.Empty;

    public ObservableCollection<StepParameterFieldViewModel> Parameters { get; } = [];

    [ObservableProperty] private bool _hasParameters;
    [ObservableProperty] private string? _unknownSummary;

    public AutomationStepRowViewModel(
        Func<AutomationStepType, IReadOnlyList<AutomationParameterSpecResponse>> specsFor,
        AutomationStepType type = AutomationStepType.Readiness,
        string? name = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        _specsFor = specsFor;
        _type = type;
        _name = name ?? string.Empty;
        RebuildFields(parameters ?? new Dictionary<string, string>());
    }

    // Změna typu kroku přegeneruje pole podle nového typu; hodnoty se stejným klíčem se přenesou.
    partial void OnTypeChanged(AutomationStepType value) => RebuildFields(CollectValues());

    /// <summary>Přegeneruje pole z katalogu (volá se i poté, co se katalog dodatečně načte).</summary>
    public void RebuildFields() => RebuildFields(CollectValues());

    private void RebuildFields(IReadOnlyDictionary<string, string> values)
    {
        var specs = _specsFor(Type);
        Parameters.Clear();
        foreach (var spec in specs)
            Parameters.Add(new StepParameterFieldViewModel(spec, values.TryGetValue(spec.Key, out var v) ? v : null));
        HasParameters = Parameters.Count > 0;

        // Klíče, které katalog nepopisuje, zachovat beze změny a ukázat je (ručně přes API / starší verze).
        var known = specs.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _unknownParameters = values.Where(kv => !known.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        UnknownSummary = _unknownParameters.Count == 0
            ? null
            : "Další parametry: " + string.Join(", ", _unknownParameters.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private Dictionary<string, string> CollectValues()
    {
        var dict = new Dictionary<string, string>(_unknownParameters, StringComparer.OrdinalIgnoreCase);
        foreach (var field in Parameters)
            if (field.Serialized is { } v)
                dict[field.Spec.Key] = v;
        return dict;
    }

    public AutomationStepInput ToInput() => new()
    {
        Type = Type,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name,
        Parameters = CollectValues(),
    };
}

/// <summary>Zaškrtávací volba jedné spouštěcí události v editoru automatizace.</summary>
public partial class EventTriggerOptionViewModel(NotificationEvent @event, string label) : ObservableObject
{
    public NotificationEvent Event { get; } = @event;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Jedna kategorie spouštěcích událostí (nadpis + volby) — místo jednoho dlouhého plochého seznamu.</summary>
public sealed record EventTriggerGroup(string Title, string Hint, IReadOnlyList<EventTriggerOptionViewModel> Options);

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
    private readonly JobProgressHubClient _hub;
    private bool _liveSubscribed;

    // Katalog kroků (typ → popis + parametry) — plní typovaná pole parametrů v editoru.
    private readonly Dictionary<AutomationStepType, AutomationStepCatalogResponse> _catalogByType = [];

    private IReadOnlyList<AutomationParameterSpecResponse> SpecsFor(AutomationStepType type) =>
        _catalogByType.TryGetValue(type, out var entry) ? entry.Parameters : [];

    /// <summary>Nový řádek kroku napojený na katalog (pole se přegenerují, i když se katalog načte později).</summary>
    private AutomationStepRowViewModel NewStepRow(
        AutomationStepType type = AutomationStepType.Readiness,
        string? name = null,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new(SpecsFor, type, name, parameters);

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
        new(NotificationEvent.JobStalled, "Porovnání se zaseklo"),
        new(NotificationEvent.ReadinessFailed, "Připravenost selhala"),
        new(NotificationEvent.HealthDegraded, "Zhoršené zdraví serveru"),
        new(NotificationEvent.StructureDrift, "Nesoulad struktury"),
        new(NotificationEvent.AutomationRecovered, "Automatizace obnovena"),
        new(NotificationEvent.AutomationFailing, "Automatizace opakovaně selhává"),
    ];

    /// <summary>Tytéž volby roztříděné do dvou srozumitelných kategorií: co se stalo s porovnáním vs. jak
    /// dopadly kontroly a automatizace. Skupiny sdílí instance voleb, takže zaškrtnutí se nikde neduplikuje.</summary>
    public IReadOnlyList<EventTriggerGroup> EventTriggerGroups { get; }

    private IReadOnlyList<EventTriggerGroup> BuildEventTriggerGroups()
    {
        NotificationEvent[] batchEvents =
            [NotificationEvent.Completed, NotificationEvent.CompletedWithErrors, NotificationEvent.GateViolated,
             NotificationEvent.Failed, NotificationEvent.JobStalled];
        return
        [
            new EventTriggerGroup(
                "Události porovnání",
                "Co se stalo s porovnáním PDF: dokončení, chyby, porušená brána, selhání nebo zaseknutí.",
                EventTriggerOptions.Where(o => batchEvents.Contains(o.Event)).ToList()),
            new EventTriggerGroup(
                "Výsledky kontrol a automatizací",
                "Jak dopadly kontroly serveru a běhy automatizací: připravenost dat, zdraví serveru, struktura, opakovaná selhání či zotavení.",
                EventTriggerOptions.Where(o => !batchEvents.Contains(o.Event)).ToList()),
        ];
    }

    [ObservableProperty] private AutomationResponse? _selected;
    [ObservableProperty] private AutomationRunResponse? _selectedRun;
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranchScope), nameof(IsInstanceScope), nameof(SaveSummary))]
    private AutomationScopeKind _scopeKind = AutomationScopeKind.Global;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    private string _branchKey = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    private string _instanceKey = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunsPreview), nameof(SaveSummary))]
    private string _cron = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunsPreview), nameof(SaveSummary))]
    private decimal _intervalSeconds = 300;

    // Schedule builder: a frequency + a local time of day (and weekday / day-of-month), translated to/from the
    // server's UTC cron by CronSchedule. Visibility of the fields below is driven off the chosen frequency.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInterval), nameof(ShowTimeOfDay), nameof(ShowWeekday),
        nameof(ShowDayOfMonth), nameof(ShowCustomCron), nameof(NextRunsPreview), nameof(SaveSummary))]
    private ScheduleKindOption? _selectedScheduleKind;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunsPreview), nameof(SaveSummary))]
    private TimeSpan? _scheduleTimeOfDay = new TimeSpan(6, 0, 0);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunsPreview), nameof(SaveSummary))]
    private WeekdayOption? _selectedWeekday;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunsPreview), nameof(SaveSummary))]
    private decimal _scheduleDayOfMonth = 1;
    [ObservableProperty] private decimal _eventDebounceSeconds = 60;
    [ObservableProperty] private decimal _timeoutSeconds = 600;
    [ObservableProperty] private decimal _maxAttempts = 1;
    [ObservableProperty] private decimal _retryDelaySeconds = 30;
    [ObservableProperty] private decimal _failureThreshold = 3;
    [ObservableProperty] private bool _eventReadinessFailed = true;
    [ObservableProperty] private bool _eventHealthDegraded = true;
    [ObservableProperty] private bool _eventStructureDrift = true;
    [ObservableProperty] private bool _eventAutomationRecovered;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    private bool _enabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    private bool _notificationsEnabled = true;
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

    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");

    /// <summary>Tři nejbližší spuštění podle aktuálního plánu (místní čas) — okamžitá kontrola, že plán sedí.</summary>
    public string NextRunsPreview
    {
        get
        {
            var kind = SelectedScheduleKind?.Kind ?? ScheduleKind.None;
            var time = ScheduleTimeOfDay ?? new TimeSpan(6, 0, 0);
            var now = DateTime.Now;
            var next = new List<DateTime>(3);
            switch (kind)
            {
                case ScheduleKind.None:
                    return "Bez časového plánu — spustí se jen na zaškrtnuté události nebo ručně.";
                case ScheduleKind.Custom:
                    return "Podle cron výrazu (UTC); náhled se pro vlastní cron nepočítá.";
                case ScheduleKind.Interval:
                    if (IntervalSeconds <= 0) return "Zadej interval větší než 0 s.";
                    for (int i = 1; i <= 3; i++) next.Add(now + TimeSpan.FromSeconds((double)IntervalSeconds * i));
                    break;
                case ScheduleKind.Daily:
                {
                    var first = now.Date + time;
                    if (first <= now) first = first.AddDays(1);
                    for (int i = 0; i < 3; i++) next.Add(first.AddDays(i));
                    break;
                }
                case ScheduleKind.Weekly:
                {
                    var day = SelectedWeekday?.Day ?? DayOfWeek.Monday;
                    var first = now.Date + time;
                    while (first.DayOfWeek != day || first <= now) first = first.AddDays(1);
                    for (int i = 0; i < 3; i++) next.Add(first.AddDays(7 * i));
                    break;
                }
                case ScheduleKind.Monthly:
                {
                    int dom = Math.Clamp((int)ScheduleDayOfMonth, 1, 28);
                    var candidate = new DateTime(now.Year, now.Month, dom) + time;
                    if (candidate <= now) candidate = candidate.AddMonths(1);
                    for (int i = 0; i < 3; i++) next.Add(candidate.AddMonths(i));
                    break;
                }
            }
            return "Příští spuštění: " + string.Join("  ·  ", next.Select(FormatRun));
        }
    }

    private static string FormatRun(DateTime t)
    {
        string day = t.Date == DateTime.Today ? "dnes"
            : t.Date == DateTime.Today.AddDays(1) ? "zítra"
            : t.ToString("ddd d. M.", Czech);
        return $"{day} {t:HH:mm}";
    }

    /// <summary>Lidská věta shrnující, co se uloží — uživatel si přečte, co automatizace skutečně udělá.</summary>
    public string SaveSummary
    {
        get
        {
            string? schedule = (SelectedScheduleKind?.Kind ?? ScheduleKind.None) switch
            {
                ScheduleKind.Interval when IntervalSeconds > 0 => $"Každých {FormatInterval((int)IntervalSeconds)}",
                ScheduleKind.Daily => $"Každý den v {Time():hh\\:mm}",
                ScheduleKind.Weekly => $"Každé {(SelectedWeekday?.Label ?? "pondělí").ToLowerInvariant()} v {Time():hh\\:mm}",
                ScheduleKind.Monthly => $"Každý měsíc {Math.Clamp((int)ScheduleDayOfMonth, 1, 28)}. v {Time():hh\\:mm}",
                ScheduleKind.Custom => "Podle cron výrazu",
                _ => null,
            };
            var triggers = EventTriggerOptions.Where(o => o.IsChecked).Select(o => $"„{o.Label}“").ToList();
            string when = (schedule, triggers.Count) switch
            {
                (null, 0) => "Jen ručně",
                (null, _) => $"Při události {string.Join(", ", triggers)}",
                (_, 0) => schedule!,
                _ => $"{schedule} a při události {string.Join(", ", triggers)}",
            };
            string scope = ScopeKind switch
            {
                AutomationScopeKind.Branch =>
                    string.IsNullOrWhiteSpace(BranchKey) ? " pro vybranou větev" : $" pro větev {BranchKey}",
                AutomationScopeKind.Instance =>
                    string.IsNullOrWhiteSpace(InstanceKey) ? " pro vybranou instanci" : $" pro instanci {BranchKey}/{InstanceKey}",
                _ => "",
            };
            var stepLabels = Steps.Select(s =>
                (string)AutomationStepTypeLabelConverter.Instance.Convert(s.Type, typeof(string), null, Czech)!);
            string notify = NotificationsEnabled
                ? "Při problému ohlásí notifikace."
                : "Notifikace jsou ztlumené.";
            string disabled = Enabled ? "" : " (zatím zakázána)";
            return $"{when}{scope} provede: {string.Join(" → ", stepLabels)}.{disabled} {notify}";

            TimeSpan Time() => ScheduleTimeOfDay ?? new TimeSpan(6, 0, 0);
        }
    }

    private static string FormatInterval(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 when seconds % 60 == 0 => $"{seconds / 60} min",
        < 3600 => $"{seconds / 60} min {seconds % 60} s",
        _ when seconds % 3600 == 0 => $"{seconds / 3600} h",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min",
    };

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

    public AutomationDefinitionsViewModel(ServerSession session, DialogService dialogs, JobProgressHubClient hub)
    {
        _session = session;
        _dialogs = dialogs;
        _hub = hub;
        EventTriggerGroups = BuildEventTriggerGroups();
        Steps.Add(NewStepRow());
        SelectedScheduleKind = ScheduleKinds[0];   // "Bez časového plánu" until a template / edit sets the cadence
        SelectedWeekday = Weekdays[0];              // Pondělí

        // Shrnutí věty reaguje i na zaškrtávátka událostí a na změny kroků (kolekce + typ kroku).
        foreach (var opt in EventTriggerOptions)
            opt.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SaveSummary));
        Steps.CollectionChanged += (_, e) =>
        {
            foreach (AutomationStepRowViewModel r in e.OldItems ?? Array.Empty<object>()) r.PropertyChanged -= OnStepRowChanged;
            foreach (AutomationStepRowViewModel r in e.NewItems ?? Array.Empty<object>()) r.PropertyChanged += OnStepRowChanged;
            OnPropertyChanged(nameof(SaveSummary));
        };
    }

    private void OnStepRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutomationStepRowViewModel.Type))
            OnPropertyChanged(nameof(SaveSummary));
    }

    /// <summary>Popisek tlačítka ztlumení podle aktuálního stavu vybrané automatizace.</summary>
    public string NotificationsToggleLabel =>
        Selected is { NotificationsEnabled: false } ? "Zapnout notifikace" : "Ztlumit notifikace";

    /// <summary>Rychlé ztlumení/zapnutí notifikací vybrané automatizace — bez otevírání editoru a bez
    /// verzovacího konfliktu s rozpracovanou úpravou (server verzi nemění).</summary>
    [RelayCommand]
    private Task ToggleNotificationsAsync() => RunAsync(async () =>
    {
        if (Selected is not { } a) throw new InvalidOperationException("Vyber automatizaci.");
        var updated = await _session.Require().SetAutomationNotificationsAsync(a.Id, !a.NotificationsEnabled);
        Info = updated.NotificationsEnabled
            ? $"Notifikace automatizace {UiText.Quote(updated.Name)} zapnuty."
            : $"Notifikace automatizace {UiText.Quote(updated.Name)} ztlumeny (běhy pokračují).";
        _dialogs.ShowToast(Info!, ToastKind.Success);
        await LiveRefreshAsync();
    });

    public Task ActivateAsync() => RunAsync(async () =>
    {
        if (!_liveSubscribed)
        {
            _hub.SystemEventReceived += OnSystemEvent;
            _liveSubscribed = true;
        }
        // System events are pushed to the scope group; join it so finished runs refresh the page live.
        try { await _hub.JoinScopeAsync(); } catch { /* realtime is best-effort */ }

        // Katalog kroků (jednou): z něj se generují typovaná pole parametrů. Bez něj editor funguje dál,
        // jen parametry zůstanou skryté a hodnoty se zachovají beze změny.
        if (_catalogByType.Count == 0)
        {
            try
            {
                foreach (var entry in await _session.Require().GetAutomationCatalogAsync())
                    _catalogByType[entry.Type] = entry;
                foreach (var row in Steps)
                    row.RebuildFields();
            }
            catch { /* katalog je vylepšení editoru, ne podmínka */ }
        }

        await LoadBranchOptionsAsync();
        await LoadAsync();
        if (TemplateGroups.Count == 0) await LoadTemplatesAsync();
    });

    public Task DeactivateAsync()
    {
        if (_liveSubscribed)
        {
            _hub.SystemEventReceived -= OnSystemEvent;
            _liveSubscribed = false;
        }
        return Task.CompletedTask;
    }

    /// <summary>Finished automation runs refresh the list + the open run history live — no F5 needed.</summary>
    private void OnSystemEvent(SystemEvent e)
    {
        if (e.Type.StartsWith("automation.", StringComparison.Ordinal))
            _ = LiveRefreshAsync();
    }

    // The live refresh preserves the selection (by id) and never touches the editor fields — those are only
    // (re)filled by an explicit Edit/New, so a background event cannot wipe an in-progress edit. A burst of
    // events (engine tick finishing several automations) coalesces into one refresh via the in-flight gate.
    private bool _liveRefreshRunning;

    private async Task LiveRefreshAsync()
    {
        if (_liveRefreshRunning) return;
        _liveRefreshRunning = true;
        try
        {
            var selectedId = Selected?.Id;
            var fresh = await _session.Require().ListAutomationsAsync();
            Automations.Clear();
            foreach (var a in fresh) Automations.Add(a);
            HasNoAutomations = Automations.Count == 0;
            OnPropertyChanged(nameof(Summary));
            if (selectedId is { } id)
                Selected = Automations.FirstOrDefault(a => a.Id == id); // re-select → OnSelectedChanged reloads runs
        }
        catch { /* best-effort live refresh; the next event or F5 retries */ }
        finally { _liveRefreshRunning = false; }
    }

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

    partial void OnSelectedChanged(AutomationResponse? value)
    {
        OnPropertyChanged(nameof(NotificationsToggleLabel));
        _ = RunAsync(LoadRunsAsync);
    }

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
        Selected = null; // odkryje galerii šablon v hlavní ploše — přirozený první krok nové automatizace
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
        Steps.Add(NewStepRow());
        foreach (var opt in EventTriggerOptions) opt.IsChecked = false;
        EventReadinessFailed = EventHealthDegraded = EventStructureDrift = true;
        EventAutomationRecovered = false;
        Enabled = true;
        NotificationsEnabled = true;
        Info = null;
    }

    /// <summary>Předvyplní editor ze šablony. Vznikne běžná (nová) automatizace — vše zůstává editovatelné.</summary>
    [RelayCommand]
    private void UseTemplate(AutomationTemplateResponse? template)
    {
        if (template is null) return;

        Selected = null; // editor teď patří nové automatizaci, ne vybranému řádku
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
        foreach (var step in template.Steps) Steps.Add(NewStepRow(step.Type, step.Name, step.Parameters));
        if (Steps.Count == 0) Steps.Add(NewStepRow());

        foreach (var opt in EventTriggerOptions) opt.IsChecked = false;
        EventReadinessFailed = template.DefaultEvents.Contains(NotificationEvent.ReadinessFailed);
        EventHealthDegraded = template.DefaultEvents.Contains(NotificationEvent.HealthDegraded);
        EventStructureDrift = template.DefaultEvents.Contains(NotificationEvent.StructureDrift);
        EventAutomationRecovered = template.DefaultEvents.Contains(NotificationEvent.AutomationRecovered);
        Enabled = true;
        NotificationsEnabled = true;
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
        foreach (var step in a.Steps) Steps.Add(NewStepRow(step.Type, step.Name, step.Parameters));
        if (Steps.Count == 0) Steps.Add(NewStepRow());
        foreach (var opt in EventTriggerOptions) opt.IsChecked = a.EventTriggers.Contains(opt.Event);
        EventReadinessFailed = a.Events.Contains(NotificationEvent.ReadinessFailed);
        EventHealthDegraded = a.Events.Contains(NotificationEvent.HealthDegraded);
        EventStructureDrift = a.Events.Contains(NotificationEvent.StructureDrift);
        EventAutomationRecovered = a.Events.Contains(NotificationEvent.AutomationRecovered);
        Enabled = a.Enabled;
        NotificationsEnabled = a.NotificationsEnabled;
        Info = $"Editace automatizace {a.Key} (v{a.Version}).";
    }

    [RelayCommand]
    private void AddStep() => Steps.Add(NewStepRow());

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
        // Klíč je technikálie — když ho uživatel nevyplní, vygeneruje se z názvu (a název je pak povinný).
        if (string.IsNullOrWhiteSpace(Key))
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new InvalidOperationException("Zadej název automatizace.");
            Key = SuggestKey(Slugify(Name));
        }
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
                Events = Events(), Enabled = Enabled, NotificationsEnabled = NotificationsEnabled,
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
                Events = Events(), Enabled = Enabled, NotificationsEnabled = NotificationsEnabled,
            });
            Info = "Vytvořeno.";
        }
        await LoadAsync();
        _dialogs.ShowToast(Info!, ToastKind.Success);
    });

    /// <summary>Z názvu udělá bezpečný klíč: malá písmena bez diakritiky, mezery a zvláštní znaky → pomlčky.</summary>
    private static string Slugify(string name)
    {
        string normalized = name.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue; // diakritika
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        string slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "automatizace" : slug;
    }

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
