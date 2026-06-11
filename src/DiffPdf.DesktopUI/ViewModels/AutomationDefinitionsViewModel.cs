using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Provides the parameter schema for a step type (from the loaded server catalog; empty until loaded).</summary>
public delegate IReadOnlyList<AutomationParameterSpecResponse> ParameterSpecProvider(AutomationStepType type);

/// <summary>
/// One typed parameter field in the step editor, driven by an <see cref="AutomationParameterSpecResponse"/>
/// (label, help, type, default, bounds). Renders as a NumericUpDown / CheckBox / ComboBox / TextBox; the
/// edited value is serialised back to the step's string parameter via <see cref="ToValue"/>.
/// </summary>
public partial class AutomationParameterFieldViewModel : ObservableObject
{
    public AutomationParameterSpecResponse Spec { get; }
    public string Key => Spec.Key;
    public string Label => Spec.Label;
    public string Help => Spec.Help;

    public bool IsInt => Spec.Type == AutomationParameterType.Int;
    public bool IsBool => Spec.Type == AutomationParameterType.Bool;
    public bool IsEnum => Spec.Type == AutomationParameterType.Enum;
    public bool IsString => Spec.Type == AutomationParameterType.String;
    public IReadOnlyList<string> EnumValues => Spec.EnumValues ?? [];
    public decimal Minimum => Spec.Min ?? decimal.MinValue;
    public decimal Maximum => Spec.Max ?? decimal.MaxValue;

    [ObservableProperty] private decimal _intValue;
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private string _stringValue = string.Empty;

    public AutomationParameterFieldViewModel(AutomationParameterSpecResponse spec, string? seedValue)
    {
        Spec = spec;
        string? v = string.IsNullOrWhiteSpace(seedValue) ? spec.Default : seedValue;
        switch (spec.Type)
        {
            case AutomationParameterType.Int:
                _intValue = decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
                break;
            case AutomationParameterType.Bool:
                _boolValue = bool.TryParse(v, out var b) && b;
                break;
            default:
                _stringValue = v ?? string.Empty;
                break;
        }
    }

    /// <summary>The current value as the string stored in the step's parameter dictionary.</summary>
    public string ToValue() => Spec.Type switch
    {
        AutomationParameterType.Int => ((long)IntValue).ToString(CultureInfo.InvariantCulture),
        AutomationParameterType.Bool => BoolValue ? "true" : "false",
        _ => StringValue.Trim(),
    };
}

/// <summary>
/// One editovatelný krok pipeline v editoru automatizace. Parametry se zobrazují jako typovaná pole řízená
/// katalogem (<see cref="ParameterSpecProvider"/>); při změně typu se pole přestaví podle nového schématu.
/// </summary>
public partial class AutomationStepRowViewModel : ObservableObject
{
    private readonly ParameterSpecProvider _specs;
    private Dictionary<string, string> _seed;

    public AutomationStepType[] Types { get; } = Enum.GetValues<AutomationStepType>();

    /// <summary>Typovaná pole pro parametry aktuálního typu kroku.</summary>
    public ObservableCollection<AutomationParameterFieldViewModel> Fields { get; } = [];

    [ObservableProperty] private AutomationStepType _type = AutomationStepType.Readiness;
    [ObservableProperty] private string _name = string.Empty;

    public AutomationStepRowViewModel(
        ParameterSpecProvider specs,
        AutomationStepType type = AutomationStepType.Readiness,
        string name = "",
        IReadOnlyDictionary<string, string>? seed = null)
    {
        _specs = specs;
        _type = type;
        _name = name;
        _seed = seed is null ? new Dictionary<string, string>() : new Dictionary<string, string>(seed);
        BuildFields();
    }

    // A manual type switch drops the previous type's parameters (they don't apply to the new type).
    partial void OnTypeChanged(AutomationStepType value)
    {
        _seed = new Dictionary<string, string>();
        BuildFields();
    }

    /// <summary>Rebuilds the typed fields from the catalog — called after the catalog finishes loading.</summary>
    public void RefreshFields() => BuildFields();

    private void BuildFields()
    {
        // Preserve any current edits and unknown/extra params across the rebuild.
        var values = new Dictionary<string, string>(_seed);
        foreach (var f in Fields) values[f.Key] = f.ToValue();

        Fields.Clear();
        foreach (var spec in _specs(Type))
        {
            values.TryGetValue(spec.Key, out var seedValue);
            Fields.Add(new AutomationParameterFieldViewModel(spec, seedValue));
        }
        _seed = values;
    }

    public AutomationStepInput ToInput() => new()
    {
        Type = Type,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name,
        Parameters = BuildParameters(),
    };

    private Dictionary<string, string> BuildParameters()
    {
        // Typed fields are authoritative; leftover seed keeps params not covered by the schema
        // (e.g. when the catalog has not loaded yet, so the original values survive a save).
        var dict = new Dictionary<string, string>(_seed);
        foreach (var f in Fields) dict[f.Key] = f.ToValue();
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

    /// <summary>Parameter schema per step type, from the server catalog (empty until loaded). Drives the typed fields.</summary>
    private IReadOnlyDictionary<AutomationStepType, IReadOnlyList<AutomationParameterSpecResponse>> _parameterSpecs =
        new Dictionary<AutomationStepType, IReadOnlyList<AutomationParameterSpecResponse>>();

    private IReadOnlyList<AutomationParameterSpecResponse> SpecsFor(AutomationStepType type) =>
        _parameterSpecs.TryGetValue(type, out var specs) ? specs : [];

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
        Steps.Add(new AutomationStepRowViewModel(SpecsFor));
    }

    public Task ActivateAsync() => RunAsync(async () =>
    {
        await LoadAsync();
        if (TemplateGroups.Count == 0) await LoadTemplatesAsync();
        if (_parameterSpecs.Count == 0) await LoadCatalogAsync();
    });

    private async Task LoadCatalogAsync()
    {
        var catalog = await _session.Require().GetAutomationCatalogAsync();
        _parameterSpecs = catalog.ToDictionary(c => c.Type, c => c.Parameters);
        foreach (var step in Steps) step.RefreshFields(); // seed fields once the schema is known
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
        Steps.Add(new AutomationStepRowViewModel(SpecsFor));
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
        foreach (var step in template.Steps) Steps.Add(new AutomationStepRowViewModel(SpecsFor, step.Type, step.Name ?? string.Empty, step.Parameters));
        if (Steps.Count == 0) Steps.Add(new AutomationStepRowViewModel(SpecsFor));

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
        foreach (var step in a.Steps) Steps.Add(new AutomationStepRowViewModel(SpecsFor, step.Type, step.Name ?? string.Empty, step.Parameters));
        if (Steps.Count == 0) Steps.Add(new AutomationStepRowViewModel(SpecsFor));
        foreach (var opt in EventTriggerOptions) opt.IsChecked = a.EventTriggers.Contains(opt.Event);
        EventReadinessFailed = a.Events.Contains(NotificationEvent.ReadinessFailed);
        EventHealthDegraded = a.Events.Contains(NotificationEvent.HealthDegraded);
        EventStructureDrift = a.Events.Contains(NotificationEvent.StructureDrift);
        EventAutomationRecovered = a.Events.Contains(NotificationEvent.AutomationRecovered);
        Enabled = a.Enabled;
        Info = $"Editace automatizace {a.Key} (v{a.Version}).";
    }

    [RelayCommand]
    private void AddStep() => Steps.Add(new AutomationStepRowViewModel(SpecsFor));

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
