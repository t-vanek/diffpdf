using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Instance pod větví: seznam + vytvoření / úprava (modální formulář) + smazání (jednotlivě i hromadně) +
/// detail (připravenost / struktura, jen pro čtení) a statistiky. U každého řádku ⚙ konfigurace a tlačítka
/// fronty (Spustit / Přidat do fronty / Pozastavit / Zastavit / Obnovit) řízená živým stavem per-branch fronty.
/// Seznam se obnovuje živými událostmi fronty/scope a na vyžádání (znovu-kliknutí na položku v navigaci, kliknutí
/// do řádku, F5).
/// </summary>
public partial class InstancesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly JobProgressHubClient _hub;
    private bool _subscribed;

    // Serializes instance-list reloads (manual button, realtime push, post-mutation) so they never race the
    // shared Instances collection.
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string? _loadedBranchKey; // which branch the current rows belong to (drives fresh-load vs reconcile)
    private readonly RowSelection<InstanceRowViewModel> _selection;

    public override string Title => "Instance";
    public override string Icon => "▦";
    public override int NavOrder => 2;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<InstanceRowViewModel> Instances { get; } = [];

    /// <summary>Filtered + sortable view over <see cref="Instances"/> that the grid binds (search box + column sort).</summary>
    public DataGridCollectionView InstancesView { get; }

    /// <summary>Statistiky vybrané instance: úlohy + porovnávání (kontroly/automatizace/spouštěče mají vlastní stránky).</summary>
    public ObservableCollection<StatGroup> Stats { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private InstanceRowViewModel? _selectedRow;
    [ObservableProperty] private string? _info;

    /// <summary>Live filter over key/name/base path (toolbar search box).</summary>
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadinessDisplay))]
    private InstanceReadiness? _readiness;

    /// <summary>Friendly, merged readiness + folder-structure view for the detail panel (null until loaded).</summary>
    public ReadinessView? ReadinessDisplay => Readiness is { } r ? ReadinessView.From(r) : null;

    /// <summary>Počet zaškrtnutých instancí pro hromadné smazání (řídí popisek + dostupnost tlačítka).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    private int _selectedDeleteCount;

    public bool CanDeleteSelected => SelectedDeleteCount > 0;

    /// <summary>True when the instance list is empty — drives the empty-state hint.</summary>
    public bool HasNoInstances => Instances.Count == 0;

    /// <summary>Vybraná instance (odvozená z vybraného řádku) — pro detailní panel a akce úprav/mazání.</summary>
    public Instance? SelectedInstance => SelectedRow?.Instance;

    // Kořen struktury nastavený na serveru (ScopeSync:RootPath); předává se do formuláře pro náhled cesty.
    [ObservableProperty] private string? _scopeRoot;

    public InstancesViewModel(ServerSession session, DialogService dialogs, JobProgressHubClient hub)
    {
        _session = session;
        _dialogs = dialogs;
        _hub = hub;
        InstancesView = new DataGridCollectionView(Instances) { Filter = MatchesSearch };
        _selection = new RowSelection<InstanceRowViewModel>(InstancesView, () => Instances, RecomputeDeleteSelection);
    }

    private bool MatchesSearch(object o)
    {
        if (o is not InstanceRowViewModel r) return false;
        var s = SearchText?.Trim();
        return string.IsNullOrEmpty(s)
            || r.Instance.Key.Contains(s, StringComparison.OrdinalIgnoreCase)
            || r.Instance.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
            || r.Instance.BasePath.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value) => InstancesView.Refresh();

    /// <summary>Header "select all": toggles the checkbox on every currently-visible (filtered) row.</summary>
    public bool AllSelected
    {
        get => _selection.AllSelected;
        set => _selection.AllSelected = value;
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        try { await _hub.EnsureStartedAsync(); } catch { /* live queue state is best-effort */ }
        if (!_subscribed)
        {
            _hub.QueueStateReceived += OnQueueState;
            _hub.TriggerEventReceived += OnScopeEvent;
            _hub.Reconnected += OnReconnected;
            _subscribed = true;
        }
        // Join the global scope group so branch/instance changes anywhere push to this page in realtime.
        try { await _hub.JoinScopeAsync(); } catch { /* realtime scope updates are best-effort */ }

        await LoadBranchesAsync();
        try { ScopeRoot = (await _session.Require().GetScopeRootAsync()).Root; } catch { /* root hint is optional */ }
    });

    // Branch changes refresh the branch picker; instance changes refresh the current branch's instance list.
    // Deduped by the load gate; errors are swallowed (the periodic auto-refresh retries).
    private void OnScopeEvent(TriggerEvent e)
    {
        if (e.EventType.StartsWith("branch.", StringComparison.Ordinal))
            _ = ReloadBranchesQuietlyAsync();
        else if (e.EventType.StartsWith("instance.", StringComparison.Ordinal))
            _ = ReloadQuietlyAsync();
    }

    public override Task DeactivateAsync()
    {
        if (_subscribed)
        {
            _hub.QueueStateReceived -= OnQueueState;
            _hub.TriggerEventReceived -= OnScopeEvent;
            _hub.Reconnected -= OnReconnected;
            _subscribed = false;
        }
        return Task.CompletedTask;
    }

    // Connection restored after a drop — refresh both the branch picker and the current instances.
    private void OnReconnected()
    {
        _ = ReloadBranchesQuietlyAsync();
        _ = ReloadQuietlyAsync();
    }

    private async Task ReloadBranchesQuietlyAsync()
    {
        try { await LoadBranchesAsync(); }
        catch { /* best-effort realtime refresh */ }
    }

    private async Task ReloadQuietlyAsync()
    {
        try { await LoadInstancesAsync(); }
        catch { /* best-effort realtime refresh */ }
    }

    private async Task LoadBranchesAsync()
    {
        var list = await _session.Require().ListBranchesAsync();

        // Reconcile the picker in place by key so a realtime refresh doesn't reset the user's selected branch.
        // Existing entries are kept (the combo binds the immutable Key); new keys are appended; removed keys
        // drop out — and if the selected branch was removed, the combo clears it (→ instances reload empty).
        var incoming = new HashSet<string>(list.Select(b => b.Key), StringComparer.Ordinal);
        for (int i = Branches.Count - 1; i >= 0; i--)
            if (!incoming.Contains(Branches[i].Key)) Branches.RemoveAt(i);

        var present = new HashSet<string>(Branches.Select(b => b.Key), StringComparer.Ordinal);
        foreach (var b in list)
            if (present.Add(b.Key)) Branches.Add(b);
    }

    /// <summary>Reloads the selected branch's instances, reconciling the rows in place (see <see cref="ReconcileInstances"/>).</summary>
    private async Task LoadInstancesAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            if (SelectedBranch is not { } branch)
            {
                ClearInstances();
                _loadedBranchKey = null;
                return;
            }
            var client = _session.Require();
            var loaded = await client.ListInstancesAsync(branch.Key);

            // A newer load (branch change / refresh) started while we awaited → let it own the grid.
            if (SelectedBranch?.Key != branch.Key) return;

            // Branch changed since the rows were built → start fresh (don't carry rows across branches);
            // same branch (an auto-refresh) → reconcile in place to keep selection + checkbox state.
            if (_loadedBranchKey != branch.Key)
            {
                ClearInstances();
                _loadedBranchKey = branch.Key;
            }
            ReconcileInstances(loaded);

            // Seed per-instance run-queue status and join the branch's SignalR group for live pushes.
            try
            {
                await _hub.JoinBranchAsync(branch.Key);
                if (SelectedBranch?.Key == branch.Key)
                    ApplyState(await client.GetBranchQueueAsync(branch.Key));
            }
            catch { /* queue state is best-effort */ }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Merges the fetched instances into <see cref="Instances"/> in place via <see cref="ListReconciler"/>
    /// (update / insert / drop by key), keeping selection + checkbox state.</summary>
    private void ReconcileInstances(IReadOnlyList<Instance> list)
    {
        ListReconciler.Reconcile(Instances, list,
            keyOf: i => i.Key,
            rowKeyOf: r => r.Instance.Key,
            create: i => new InstanceRowViewModel(i),
            update: (r, i) => r.Instance = i, // refresh name/basePath/enabled (key is the immutable identity)
            onAdded: r => _selection.Track(r),
            onRemoved: r => _selection.Untrack(r));
        RecomputeDeleteSelection();
    }

    private void ClearInstances()
    {
        foreach (var r in Instances) _selection.Untrack(r);
        Instances.Clear();
        RecomputeDeleteSelection();
    }

    private void RecomputeDeleteSelection()
    {
        SelectedDeleteCount = _selection.SelectedCount;
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(HasNoInstances));
    }

    private void OnQueueState(BranchQueueState state)
    {
        if (state.BranchKey == SelectedBranch?.Key)
            ApplyState(state);
    }

    private void ApplyState(BranchQueueState? state)
    {
        foreach (var row in Instances)
            row.Apply(state?.Instances.FirstOrDefault(i => i.InstanceKey == row.Instance.Key));
    }

    partial void OnSelectedBranchChanged(Branch? value) => _ = RunAsync(LoadInstancesAsync);

    /// <summary>Reloads the branch picker + the current branch's instances on demand (nav re-click / row click / F5).</summary>
    public override Task ReloadAsync() => RunAsync(async () =>
    {
        var keep = SelectedBranch?.Key;
        await LoadBranchesAsync();
        SelectedBranch = keep is not null ? Branches.FirstOrDefault(b => b.Key == keep) : null;
        await LoadInstancesAsync(); // belt-and-braces: also reload directly (the gate coalesces with the selection-change load)
    });

    // ---------------- Run queue (per instance) ----------------

    [RelayCommand] private Task RunInstance(InstanceRowViewModel row) => QueueInstanceAsync(row, QueueAction.Run);
    [RelayCommand] private Task EnqueueInstance(InstanceRowViewModel row) => QueueInstanceAsync(row, QueueAction.Enqueue);
    [RelayCommand] private Task PauseInstance(InstanceRowViewModel row) => QueueInstanceAsync(row, QueueAction.Pause);
    [RelayCommand] private Task ResumeInstance(InstanceRowViewModel row) => QueueInstanceAsync(row, QueueAction.Resume);
    [RelayCommand] private Task StopInstance(InstanceRowViewModel row) => QueueInstanceAsync(row, QueueAction.Stop);

    private Task QueueInstanceAsync(InstanceRowViewModel? row, QueueAction action) => RunAsync(async () =>
    {
        if (row is null || SelectedBranch is not { } branch) return;
        var outcome = await _session.Require().QueueInstanceAsync(branch.Key, row.Instance.Key, action);
        ApplyState(outcome.State);
        if (!string.IsNullOrEmpty(outcome.Message)) Info = outcome.Message;
    });

    /// <summary>Otevře formulář pro vytvoření nové instance pod vybranou větví (modální dialog).</summary>
    [RelayCommand]
    private async Task ShowCreate()
    {
        if (SelectedBranch is not { } branch) { Info = "Vyber větev."; return; }
        var keys = Instances.Select(r => r.Instance.Key).ToList();
        var names = Instances.Select(r => r.Instance.Name).ToList();
        var form = InstanceFormViewModel.ForCreate(_session, branch.Key, ScopeRoot, keys, names);
        if (await _dialogs.ShowInstanceFormAsync(form))
        {
            Info = $"Instance '{form.Key.Trim()}' vytvořena.";
            await RunAsync(LoadInstancesAsync);
        }
    }

    /// <summary>Otevře formulář pro úpravu vybrané (nebo předané) instance (modální dialog).</summary>
    [RelayCommand]
    private async Task EditInstance(InstanceRowViewModel? row)
    {
        if (SelectedBranch is not { } branch) return;
        if ((row ?? SelectedRow)?.Instance is not { } inst) return;
        var otherNames = Instances.Where(r => r.Instance.Key != inst.Key).Select(r => r.Instance.Name).ToList();
        var form = InstanceFormViewModel.ForEdit(_session, branch.Key, inst, otherNames);
        if (await _dialogs.ShowInstanceFormAsync(form))
        {
            Info = $"Instance '{inst.Key}' uložena.";
            await RunAsync(LoadInstancesAsync);
        }
    }

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is not { } branch || SelectedInstance is not { } inst)
            throw new InvalidOperationException("Vyber větev i instanci.");
        if (!await _dialogs.ConfirmAsync("Smazat instanci", $"Opravdu smazat instanci '{inst.Key}'?"))
            return;
        await _session.Require().DeleteInstanceAsync(branch.Key, inst.Key);
        Info = $"Instance '{inst.Key}' smazána.";
        await LoadInstancesAsync();
    });

    /// <summary>Hromadné smazání všech zaškrtnutých instancí. Pokračuje i když některá selže (např. má úlohu).</summary>
    [RelayCommand]
    private Task DeleteSelectedAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is not { } branch) { Info = "Vyber větev."; return; }
        var targets = Instances.Where(r => r.IsSelected).Select(r => r.Instance).ToList();
        if (targets.Count == 0) { Info = "Nevybral jsi žádnou instanci."; return; }
        if (!await _dialogs.ConfirmAsync("Smazat instance", $"Opravdu smazat vybrané instance ({targets.Count})?"))
            return;

        var client = _session.Require();
        int deleted = 0;
        var failed = new List<string>();
        foreach (var inst in targets)
        {
            try { await client.DeleteInstanceAsync(branch.Key, inst.Key); deleted++; }
            catch (DiffPdfApiException ex) { failed.Add($"{inst.Key} ({ex.Detail ?? ex.StatusCode.ToString()})"); }
        }

        Info = failed.Count == 0
            ? $"Smazáno {deleted} instancí."
            : $"Smazáno {deleted}, nešlo smazat {failed.Count}: {string.Join("; ", failed)}";
        await LoadInstancesAsync();
    });

    /// <summary>Hromadně povolí všechny zaškrtnuté instance.</summary>
    [RelayCommand]
    private Task EnableSelectedAsync() => SetSelectedEnabledAsync(true);

    /// <summary>Hromadně zakáže všechny zaškrtnuté instance.</summary>
    [RelayCommand]
    private Task DisableSelectedAsync() => SetSelectedEnabledAsync(false);

    private Task SetSelectedEnabledAsync(bool enabled) => RunAsync(async () =>
    {
        if (SelectedBranch is not { } branch) { Info = "Vyber větev."; return; }
        var targets = Instances.Where(r => r.IsSelected).Select(r => r.Instance).ToList();
        if (targets.Count == 0) { Info = "Nevybral jsi žádnou instanci."; return; }

        var client = _session.Require();
        int done = 0;
        var failed = new List<string>();
        foreach (var inst in targets)
        {
            try
            {
                await client.UpdateInstanceAsync(branch.Key, inst.Key,
                    new UpdateInstanceRequest(inst.Name, inst.BasePath, inst.CredentialProfile, enabled, inst.Version));
                done++;
            }
            catch (DiffPdfApiException ex) { failed.Add($"{inst.Key} ({ex.Detail ?? ex.StatusCode.ToString()})"); }
        }

        Info = failed.Count == 0
            ? $"{(enabled ? "Povoleno" : "Zakázáno")}: {done} instancí."
            : $"Hotovo {done}, selhalo {failed.Count}: {string.Join("; ", failed)}";
        await LoadInstancesAsync();
    });

    // Výběr instance načte (jen pro čtení) připravenost, inspekci struktury old/new/reports a statistiky.
    partial void OnSelectedRowChanged(InstanceRowViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedInstance));
        Stats.Clear();
        if (value is null || SelectedBranch is null)
        {
            Readiness = null;
            return;
        }
        _ = RunAsync(LoadInstanceDetailsAsync);
    }

    private async Task LoadInstanceDetailsAsync()
    {
        // Snapshot the selection up front. Each await below yields, and the user can pick another row (or a
        // refresh can clear the grid) meanwhile — re-reading SelectedBranch/SelectedInstance after an await
        // would then dereference a now-null selection (NRE) or load one instance's details onto another.
        if (SelectedBranch is not { } branch || SelectedInstance is not { } instance)
            return;
        var client = _session.Require();

        var readiness = await client.GetReadinessAsync(branch.Key, instance.Key, sampleSize: 10);
        var stats = await client.GetInstanceStatsAsync(branch.Key, instance.Key);

        // Selection moved on while we loaded → discard these now-stale results; the current selection's own
        // load populates the panel. (OnSelectedRowChanged already cleared Stats on the change.)
        if (SelectedInstance is not { } current || current.Key != instance.Key || SelectedBranch?.Key != branch.Key)
            return;

        Readiness = readiness;
        Stats.Clear();
        foreach (var g in ScopeStatGroups.JobsAndComparisons(stats)) Stats.Add(g);
    }

    /// <summary>Otevře konfiguraci (triggery + porovnávače) této instance (ozubené kolo u řádku).</summary>
    [RelayCommand]
    private async Task OpenSettingsAsync(InstanceRowViewModel? row)
    {
        if (row is null || SelectedBranch is null) return;
        var vm = new ScopeSettingsViewModel(_session, ConfigScopeLevel.Instance, SelectedBranch.Key, row.Instance.Key, isModal: true);
        await vm.LoadAsync();
        await _dialogs.ShowScopeSettingsAsync(vm);
    }
}
