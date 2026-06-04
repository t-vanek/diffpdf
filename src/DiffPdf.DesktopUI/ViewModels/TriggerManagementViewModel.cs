using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Spouštěče — samostatná sekce pro správu spouštěčů jako entit: seznam s filtry (větev / instance / stav),
/// detail vybraného spouštěče s úpravou a akcemi (spustit, povolit, zakázat, smazat), historií běhů, vytvořenými
/// úlohami a auditem. Reaguje na real-time události (SignalR) — řádky i detail se aktualizují automaticky a
/// dokončení/selhání porovnání se objeví v oznámeních.
/// </summary>
public partial class TriggerManagementViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly NavigationService _navigation;
    private readonly JobProgressHubClient _hub;

    private bool _subscribed;
    private readonly HashSet<string> _joinedBranches = new(StringComparer.Ordinal);
    // Předvolba nastavená při příchodu z detailu instance (GoTo) — aplikuje se po načtení větví.
    private string? _pendingBranchKey;
    private string? _pendingInstanceKey;

    public override string Title => "Spouštěče";
    public override int NavOrder => 4;

    public ObservableCollection<Branch> Branches { get; } = [];
    /// <summary>Instance pod zvolenou větví ve filtru.</summary>
    public ObservableCollection<Instance> FilterInstances { get; } = [];
    /// <summary>Instance pod zvolenou větví v zakládacím formuláři.</summary>
    public ObservableCollection<Instance> CreateInstances { get; } = [];
    public ObservableCollection<TriggerResponse> Triggers { get; } = [];
    public ObservableCollection<TriggerRunResponse> Runs { get; } = [];
    public ObservableCollection<JobSummary> Jobs { get; } = [];
    public ObservableCollection<AuditEntryResponse> Audit { get; } = [];
    /// <summary>Posledních pár real-time oznámení (nejnovější nahoře).</summary>
    public ObservableCollection<string> Notifications { get; } = [];

    public IReadOnlyList<string> StatusFilters { get; } =
        ["(vše)", "Active", "Inactive", "Disabled", "Running", "Pending", "Error", "Deleted"];

    // ---- Filtry ----
    [ObservableProperty] private Branch? _filterBranch;
    [ObservableProperty] private Instance? _filterInstance;
    [ObservableProperty] private string _selectedStatusFilter = "(vše)";
    [ObservableProperty] private bool _includeDeleted;

    [ObservableProperty] private TriggerResponse? _selectedTrigger;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private string? _liveStatus;

    // ---- Zakládací formulář ----
    [ObservableProperty] private bool _showCreateForm;
    [ObservableProperty] private Branch? _newBranch;
    [ObservableProperty] private Instance? _newInstance;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newDescription = string.Empty;
    [ObservableProperty] private bool _newEnabled = true;
    [ObservableProperty] private string _newSearchPattern = "*.pdf";
    [ObservableProperty] private bool _newRecursive = true;
    [ObservableProperty] private string? _validationError;

    // ---- Úprava vybraného spouštěče ----
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editDescription = string.Empty;
    [ObservableProperty] private bool _editEnabled = true;
    [ObservableProperty] private string _editSearchPattern = "*.pdf";
    [ObservableProperty] private bool _editRecursive = true;

    public TriggerManagementViewModel(ServerSession session, DialogService dialogs,
        NavigationService navigation, JobProgressHubClient hub)
    {
        _session = session;
        _dialogs = dialogs;
        _navigation = navigation;
        _hub = hub;
    }

    /// <summary>Otevře sekci se seznamem předfiltrovaným na danou instanci (volá detail instance přes navigaci).</summary>
    public void FocusInstance(string branchKey, string instanceKey)
    {
        _pendingBranchKey = branchKey;
        _pendingInstanceKey = instanceKey;
    }

    /// <summary>Otevře sekci se seznamem předfiltrovaným na celou větev (volá detail větve přes navigaci).</summary>
    public void FocusBranch(string branchKey)
    {
        _pendingBranchKey = branchKey;
        _pendingInstanceKey = null;
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        await LoadBranchesAsync();

        // Real-time: odběr událostí spouštěčů (jen jednou).
        try
        {
            await _hub.EnsureStartedAsync();
            if (!_subscribed)
            {
                _hub.TriggerEventReceived += OnTriggerEvent;
                _subscribed = true;
            }
        }
        catch { /* real-time je best-effort */ }

        // Předvolba z detailu instance má přednost před aktuálním filtrem.
        if (_pendingBranchKey is { } bk)
        {
            FilterBranch = Branches.FirstOrDefault(b => string.Equals(b.Key, bk, StringComparison.Ordinal));
            await LoadFilterInstancesAsync();
            if (_pendingInstanceKey is { } ik)
                FilterInstance = FilterInstances.FirstOrDefault(i => string.Equals(i.Key, ik, StringComparison.Ordinal));
            _pendingBranchKey = _pendingInstanceKey = null;
        }

        await LoadTriggersAsync();
    });

    private async Task LoadBranchesAsync()
    {
        var list = await _session.Require().ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(b);
    }

    private async Task LoadFilterInstancesAsync()
    {
        FilterInstances.Clear();
        if (FilterBranch is null) return;
        foreach (var i in await _session.Require().ListInstancesAsync(FilterBranch.Key))
            FilterInstances.Add(i);
    }

    private async Task LoadTriggersAsync()
    {
        var client = _session.Require();
        TriggerStatus? status = SelectedStatusFilter == "(vše)" ? null : Enum.Parse<TriggerStatus>(SelectedStatusFilter);
        bool includeDeleted = IncludeDeleted || status == TriggerStatus.Deleted;

        IReadOnlyList<TriggerResponse> list =
            FilterInstance is { } inst ? await client.ListTriggersAsync(instanceId: inst.Id, status: status, includeDeleted: includeDeleted)
            : FilterBranch is { } br ? await client.ListTriggersAsync(branchId: br.Id, status: status, includeDeleted: includeDeleted)
            : await client.ListTriggersAsync(status: status, includeDeleted: includeDeleted);

        var previouslySelected = SelectedTrigger?.Id;
        Triggers.Clear();
        foreach (var t in list) Triggers.Add(t);
        if (previouslySelected is { } id)
            SelectedTrigger = Triggers.FirstOrDefault(t => t.Id == id);

        await JoinScopesAsync();
    }

    /// <summary>Připojí se k větvovým skupinám přítomným v seznamu, aby řádky chodily real-time.</summary>
    private async Task JoinScopesAsync()
    {
        foreach (var key in Triggers.Select(t => t.BranchKey).Distinct(StringComparer.Ordinal))
        {
            if (_joinedBranches.Add(key))
            {
                try { await _hub.JoinBranchAsync(key); } catch { /* best-effort */ }
            }
        }
    }

    partial void OnFilterBranchChanged(Branch? value) => _ = RunAsync(async () =>
    {
        FilterInstance = null;
        await LoadFilterInstancesAsync();
        await LoadTriggersAsync();
    });

    partial void OnFilterInstanceChanged(Instance? value) => _ = RunAsync(LoadTriggersAsync);
    partial void OnSelectedStatusFilterChanged(string value) => _ = RunAsync(LoadTriggersAsync);
    partial void OnIncludeDeletedChanged(bool value) => _ = RunAsync(LoadTriggersAsync);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadBranchesAsync();
        await LoadTriggersAsync();
    });

    // ---------------- Detail vybraného spouštěče ----------------

    partial void OnSelectedTriggerChanged(TriggerResponse? value)
    {
        Runs.Clear();
        Jobs.Clear();
        Audit.Clear();
        if (value is null) return;
        EditName = value.Name;
        EditDescription = value.Description ?? string.Empty;
        EditEnabled = value.Enabled;
        EditSearchPattern = value.Spec.SearchPattern;
        EditRecursive = value.Spec.Recursive;
        _ = RunAsync(() => LoadDetailAsync(value.Id));
    }

    private async Task LoadDetailAsync(Guid triggerId)
    {
        var client = _session.Require();
        try { await _hub.JoinTriggerAsync(triggerId); } catch { /* best-effort */ }

        Runs.Clear();
        foreach (var r in await client.ListTriggerRunsAsync(triggerId, limit: 50)) Runs.Add(r);
        Jobs.Clear();
        foreach (var j in await client.ListTriggerJobsAsync(triggerId)) Jobs.Add(j);
        Audit.Clear();
        foreach (var a in await client.ListTriggerAuditAsync(triggerId)) Audit.Add(a);
    }

    // ---------------- Vytvoření ----------------

    [RelayCommand]
    private void ShowCreate()
    {
        NewBranch = FilterBranch;
        NewName = NewDescription = string.Empty;
        NewSearchPattern = "*.pdf";
        NewRecursive = NewEnabled = true;
        ValidationError = null;
        Info = null;
        ShowCreateForm = true;
        _ = RunAsync(LoadCreateInstancesAsync);
    }

    [RelayCommand]
    private void CancelCreate()
    {
        ShowCreateForm = false;
        ValidationError = null;
    }

    partial void OnNewBranchChanged(Branch? value) => _ = RunAsync(LoadCreateInstancesAsync);

    private async Task LoadCreateInstancesAsync()
    {
        CreateInstances.Clear();
        if (NewBranch is null) return;
        foreach (var i in await _session.Require().ListInstancesAsync(NewBranch.Key))
            CreateInstances.Add(i);
        NewInstance ??= CreateInstances.FirstOrDefault();
    }

    [RelayCommand]
    private Task SaveCreateAsync() => RunAsync(async () =>
    {
        if (NewBranch is null || NewInstance is null) { ValidationError = "Vyber větev i instanci."; return; }
        if (string.IsNullOrWhiteSpace(NewName)) { ValidationError = "Zadej název spouštěče."; return; }
        ValidationError = null;

        var created = await _session.Require().CreateTriggerAsync(new CreateTriggerRequest
        {
            BranchKey = NewBranch.Key,
            InstanceKey = NewInstance.Key,
            Name = NewName.Trim(),
            Description = string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim(),
            Enabled = NewEnabled,
            Spec = new TriggerSpec { SearchPattern = NewSearchPattern.Trim(), Recursive = NewRecursive },
        });
        Info = $"Spouštěč '{created.Name}' vytvořen.";
        ShowCreateForm = false;
        await LoadTriggersAsync();
        SelectedTrigger = Triggers.FirstOrDefault(t => t.Id == created.Id);
    });

    // ---------------- Úprava ----------------

    [RelayCommand]
    private Task SaveEditAsync() => RunAsync(async () =>
    {
        if (SelectedTrigger is not { } t) throw new InvalidOperationException("Vyber spouštěč v seznamu.");
        var updated = await _session.Require().UpdateTriggerAsync(t.Id, new UpdateTriggerRequest
        {
            Name = EditName.Trim(),
            Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim(),
            Enabled = EditEnabled,
            Spec = new TriggerSpec { SearchPattern = EditSearchPattern.Trim(), Recursive = EditRecursive },
            Version = t.Version,
        });
        Info = $"Spouštěč '{updated.Name}' uložen.";
        await LoadTriggersAsync();
        SelectedTrigger = Triggers.FirstOrDefault(x => x.Id == updated.Id);
    });

    // ---------------- Akce ----------------

    [RelayCommand]
    private Task RunTrigger() => RunAsync(async () =>
    {
        if (SelectedTrigger is not { } t) throw new InvalidOperationException("Vyber spouštěč v seznamu.");
        var result = await _session.Require().RunTriggerAsync(t.Id);
        Info = result.Success
            ? $"Spuštěno: {result.Status}" + (result.BatchJobId is { } id ? $" (úloha {id})" : "")
            : $"Nespuštěno: {result.Message}";
        if (result.BatchJobId is { } jobId)
            _navigation.GoTo<JobsViewModel>(j => j.OpenJob(jobId));
    });

    [RelayCommand]
    private Task EnableAsync() => ToggleAsync(enable: true);

    [RelayCommand]
    private Task DisableAsync() => ToggleAsync(enable: false);

    private Task ToggleAsync(bool enable) => RunAsync(async () =>
    {
        if (SelectedTrigger is not { } t) throw new InvalidOperationException("Vyber spouštěč v seznamu.");
        var client = _session.Require();
        var updated = enable ? await client.EnableTriggerAsync(t.Id) : await client.DisableTriggerAsync(t.Id);
        Info = $"Spouštěč '{updated.Name}' {(enable ? "povolen" : "zakázán")}.";
        await LoadTriggersAsync();
        SelectedTrigger = Triggers.FirstOrDefault(x => x.Id == updated.Id);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedTrigger is not { } t) throw new InvalidOperationException("Vyber spouštěč v seznamu.");
        if (!await _dialogs.ConfirmAsync("Smazat spouštěč",
                $"Opravdu smazat spouštěč '{t.Name}'? Historie běhů a úloh zůstane zachována."))
            return;
        await _session.Require().DeleteTriggerAsync(t.Id);
        Info = $"Spouštěč '{t.Name}' smazán.";
        await LoadTriggersAsync();
    });

    [RelayCommand]
    private void OpenJob(JobSummary? job)
    {
        if (job is { } j) _navigation.GoTo<JobsViewModel>(v => v.OpenJob(j.Id));
    }

    // ---------------- Real-time ----------------

    private void OnTriggerEvent(TriggerEvent evt)
    {
        Note(evt);

        // Branch/instance status změny nemají konkrétní spouštěč.
        if (evt.TriggerId == Guid.Empty) return;

        // Aktualizuj řádek (a detail, je-li vybraný) podle aktuálního stavu ze serveru.
        _ = RefreshTriggerRowAsync(evt.TriggerId);
    }

    private async Task RefreshTriggerRowAsync(Guid triggerId)
    {
        var index = -1;
        for (int i = 0; i < Triggers.Count; i++)
            if (Triggers[i].Id == triggerId) { index = i; break; }
        if (index < 0) return;

        try
        {
            var fresh = await _session.Require().GetTriggerAsync(triggerId);
            if (fresh is null) return;
            bool wasSelected = SelectedTrigger?.Id == triggerId;
            Triggers[index] = fresh;
            if (wasSelected)
            {
                SelectedTrigger = fresh;
                await LoadDetailAsync(triggerId);
            }
        }
        catch { /* refresh řádku je best-effort */ }
    }

    private void Note(TriggerEvent evt)
    {
        var when = (evt.FinishedAt ?? evt.StartedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm:ss");
        var scope = evt.InstanceKey is { Length: > 0 } ik ? $" {ik}" : "";
        var detail = evt.Result ?? evt.Status ?? evt.Message;
        var line = $"[{when}] {evt.EventType}{scope}{(detail is { Length: > 0 } ? $" — {detail}" : "")}";
        Notifications.Insert(0, line);
        while (Notifications.Count > 100) Notifications.RemoveAt(Notifications.Count - 1);
        LiveStatus = line;
    }
}
