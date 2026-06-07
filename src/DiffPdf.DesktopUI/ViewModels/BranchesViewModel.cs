using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Větve: seznam + vytvoření / úprava (modální formulář) + smazání (jednotlivě i hromadně) + detail se
/// statistikami, a u každého řádku ⚙ konfigurace a tlačítka fronty (Spustit / Přidat do fronty / Pozastavit /
/// Zastavit / Obnovit) řízená živým stavem fronty. Seznam se navíc periodicky obnovuje na pozadí (vedle
/// ručního tlačítka Obnovit).
/// </summary>
public partial class BranchesViewModel : PageViewModel
{
    private const int AutoRefreshSeconds = 10;

    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly JobProgressHubClient _hub;
    private bool _subscribed;
    private bool _autoRefreshStarted;

    // Serializes list reloads: the manual button, the periodic auto-refresh, and post-mutation reloads can
    // overlap, and the rebuild mutates the shared Branches collection — so only one runs at a time.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public override string Title => "Větve";
    public override string Icon => "⋔";
    public override int NavOrder => 1;

    public ObservableCollection<BranchRowViewModel> Branches { get; } = [];

    /// <summary>Filtered + sortable view over <see cref="Branches"/> that the grid binds (search box + column sort).</summary>
    public DataGridCollectionView BranchesView { get; }

    /// <summary>Statistiky vybrané větve: úlohy + porovnávání (kontroly/automatizace/spouštěče mají vlastní stránky).</summary>
    public ObservableCollection<StatGroup> Stats { get; } = [];

    [ObservableProperty] private BranchRowViewModel? _selectedRow;
    [ObservableProperty] private string? _info;

    /// <summary>Live filter over key/name (toolbar search box).</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Počet zaškrtnutých větví pro hromadné smazání (řídí popisek + dostupnost tlačítka).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    private int _selectedDeleteCount;

    public bool CanDeleteSelected => SelectedDeleteCount > 0;

    /// <summary>True when there are no branches — drives the empty-state hint.</summary>
    public bool HasNoBranches => Branches.Count == 0;

    /// <summary>Vybraná větev (odvozená z vybraného řádku) — pro detailní panel a akce úprav/mazání.</summary>
    public Branch? SelectedBranch => SelectedRow?.Branch;

    // Kořen struktury nastavený na serveru (ScopeSync:RootPath); předává se do formuláře pro náhled složky.
    [ObservableProperty] private string? _scopeRoot;

    public BranchesViewModel(ServerSession session, DialogService dialogs, JobProgressHubClient hub)
    {
        _session = session;
        _dialogs = dialogs;
        _hub = hub;
        BranchesView = new DataGridCollectionView(Branches) { Filter = MatchesSearch };
    }

    private bool MatchesSearch(object o)
    {
        if (o is not BranchRowViewModel r) return false;
        var s = SearchText?.Trim();
        return string.IsNullOrEmpty(s)
            || r.Branch.Key.Contains(s, StringComparison.OrdinalIgnoreCase)
            || r.Branch.Name.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value) => BranchesView.Refresh();

    /// <summary>Header "select all": toggles the checkbox on every currently-visible (filtered) row.</summary>
    public bool AllSelected
    {
        get => BranchesView.Count > 0 && BranchesView.Cast<BranchRowViewModel>().All(r => r.IsSelected);
        set { foreach (var r in BranchesView.Cast<BranchRowViewModel>()) r.IsSelected = value; }
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
        // Join the global scope group so branch/instance changes anywhere (incl. a branch created elsewhere
        // we have never joined) push to this page in realtime.
        try { await _hub.JoinScopeAsync(); } catch { /* realtime scope updates are best-effort */ }

        // Start the periodic refresh up front: even if the initial load below fails (server momentarily down),
        // the loop keeps ticking — guarded + retried — and recovers on its own, so the list never stays stale.
        StartAutoRefresh();

        await LoadAsync();
        try { ScopeRoot = (await _session.Require().GetScopeRootAsync()).Root; } catch { /* root hint is optional */ }
    });

    // A branch/instance was created/updated/deleted somewhere — refresh the list right away instead of waiting
    // for the next poll. Deduped by the load gate; errors are swallowed (the periodic auto-refresh retries).
    private void OnScopeEvent(TriggerEvent e)
    {
        if (e.EventType.StartsWith("branch.", StringComparison.Ordinal) ||
            e.EventType.StartsWith("instance.", StringComparison.Ordinal))
            _ = ReloadQuietlyAsync();
    }

    // Connection restored after a drop — reload to catch any changes missed while offline.
    private void OnReconnected() => _ = ReloadQuietlyAsync();

    private async Task ReloadQuietlyAsync()
    {
        try { await LoadAsync(); }
        catch { /* best-effort realtime refresh */ }
    }

    /// <summary>Reloads the branch list, reconciling the existing rows in place (see <see cref="Reconcile"/>).</summary>
    private async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try { Reconcile(await _session.Require().ListBranchSummariesAsync()); }
        finally { _loadGate.Release(); }
    }

    /// <summary>
    /// Merges the fetched summaries into <see cref="Branches"/> in place via <see cref="ListReconciler"/>:
    /// updates existing rows (name/enabled, instance count, queue), inserts new ones (wiring selection + joining
    /// their SignalR group), drops removed — keeping selection + checkbox state across an auto-refresh. Queue +
    /// instance count come from the single summary call, so there is no per-branch round-trip (N+1).
    /// </summary>
    private void Reconcile(IReadOnlyList<BranchSummary> summaries)
    {
        ListReconciler.Reconcile(Branches, summaries,
            keyOf: s => s.Branch.Key,
            rowKeyOf: r => r.Branch.Key,
            create: s => { var row = new BranchRowViewModel(s.Branch) { InstanceCount = s.InstanceCount }; row.Apply(s.Queue); return row; },
            update: (r, s) => { r.Branch = s.Branch; r.InstanceCount = s.InstanceCount; r.Apply(s.Queue); },
            onAdded: r => { r.PropertyChanged += OnRowChanged; _ = JoinBranchQuietlyAsync(r.Branch.Key); },
            onRemoved: r => r.PropertyChanged -= OnRowChanged);
        RecomputeDeleteSelection();
        OnPropertyChanged(nameof(HasNoBranches));
    }

    private async Task JoinBranchQuietlyAsync(string key)
    {
        try { await _hub.JoinBranchAsync(key); } catch { /* live queue pushes are best-effort */ }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BranchRowViewModel.IsSelected))
            RecomputeDeleteSelection();
    }

    private void RecomputeDeleteSelection()
    {
        SelectedDeleteCount = Branches.Count(r => r.IsSelected);
        OnPropertyChanged(nameof(AllSelected));
    }

    private void StartAutoRefresh()
    {
        if (_autoRefreshStarted) return;
        _autoRefreshStarted = true;
        _ = AutoRefreshLoopAsync();
    }

    private async Task AutoRefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(AutoRefreshSeconds));
        while (await timer.WaitForNextTickAsync())
        {
            // Quiet background refresh: skip while disconnected, and never toggle the busy indicator or surface
            // errors — that is the manual "Obnovit" button's job. A failed tick is simply retried next interval.
            if (!_session.IsConnected) continue;
            try { await LoadAsync(); } catch { /* transient / disconnected */ }
        }
    }

    private void OnQueueState(BranchQueueState state)
    {
        foreach (var row in Branches)
            if (row.Branch.Key == state.BranchKey)
                row.Apply(state);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);

    /// <summary>Dry-run scope sync: report what the on-disk &lt;root&gt;/&lt;branch&gt;/&lt;instance&gt; tree would change (no writes).</summary>
    [RelayCommand]
    private Task PreviewSyncAsync() => RunAsync(async () =>
        Info = SummarizeSync(await _session.Require().SyncScopeAsync(apply: false)));

    /// <summary>Apply scope sync: register discovered branches/instances + create the missing skeleton, then reload the list.</summary>
    [RelayCommand]
    private Task ApplySyncAsync() => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Synchronizovat větve a instance",
                "Zaregistruje nalezené složky (větve a instance) do databáze a vytvoří chybějící old/new/reports na disku. Pokračovat?"))
            return;
        Info = SummarizeSync(await _session.Require().SyncScopeAsync(apply: true));
        await LoadAsync(); // nově zaregistrované větve/instance se objeví v seznamu
    });

    private static string SummarizeSync(ScopeSyncReport r)
    {
        if (!r.Reachable)
            return $"Kořen nedostupný: {r.Error}";
        int branches = r.Branches.Count(b => b.State == BranchSyncState.Registered);
        int instances = r.Branches.Sum(b => b.Instances.Count(i => i.State == InstanceSyncState.Registered));
        string verb = r.Applied ? "zaregistrováno" : "k registraci";
        return $"{r.Root}: {branches} větv(í), {instances} instancí {verb}; "
             + $"{r.MissingFolders.Count} chybějících složek, {r.OutOfRoot.Count} mimo kořen.";
    }

    partial void OnSelectedRowChanged(BranchRowViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedBranch));
        Stats.Clear();
        if (value is null) return;
        _ = RunAsync(LoadStatsAsync);
    }

    private async Task LoadStatsAsync()
    {
        Stats.Clear();
        if (SelectedBranch is not { } b) return;
        var stats = await _session.Require().GetBranchStatsAsync(b.Key);
        foreach (var g in ScopeStatGroups.JobsAndComparisons(stats)) Stats.Add(g);
    }

    // ---------------- Run queue (cascades to all enabled instances of the branch) ----------------

    [RelayCommand] private Task RunBranch(BranchRowViewModel row) => QueueBranchAsync(row, QueueAction.Run);
    [RelayCommand] private Task EnqueueBranch(BranchRowViewModel row) => QueueBranchAsync(row, QueueAction.Enqueue);
    [RelayCommand] private Task PauseBranch(BranchRowViewModel row) => QueueBranchAsync(row, QueueAction.Pause);
    [RelayCommand] private Task ResumeBranch(BranchRowViewModel row) => QueueBranchAsync(row, QueueAction.Resume);
    [RelayCommand] private Task StopBranch(BranchRowViewModel row) => QueueBranchAsync(row, QueueAction.Stop);

    private Task QueueBranchAsync(BranchRowViewModel? row, QueueAction action) => RunAsync(async () =>
    {
        if (row is null) return;
        var outcome = await _session.Require().QueueBranchAsync(row.Branch.Key, action);
        row.Apply(outcome.State);
        if (!string.IsNullOrEmpty(outcome.Message)) Info = outcome.Message;
    });

    /// <summary>Otevře konfiguraci (triggery + porovnávače) této větve (ozubené kolo u řádku).</summary>
    [RelayCommand]
    private async Task OpenSettingsAsync(BranchRowViewModel? row)
    {
        if (row is null) return;
        var vm = new ScopeSettingsViewModel(_session, ConfigScopeLevel.Branch, row.Branch.Key, instanceKey: null, isModal: true);
        await vm.LoadAsync();
        await _dialogs.ShowScopeSettingsAsync(vm);
    }

    /// <summary>Otevře formulář pro vytvoření nové větve (modální dialog).</summary>
    [RelayCommand]
    private async Task ShowCreate()
    {
        var keys = Branches.Select(r => r.Branch.Key).ToList();
        var names = Branches.Select(r => r.Branch.Name).ToList();
        var form = BranchFormViewModel.ForCreate(_session, ScopeRoot, keys, names);
        if (await _dialogs.ShowBranchFormAsync(form))
        {
            Info = $"Větev '{form.Key.Trim()}' vytvořena.";
            await RunAsync(LoadAsync);
        }
    }

    /// <summary>Otevře formulář pro úpravu vybrané (nebo předané) větve (modální dialog).</summary>
    [RelayCommand]
    private async Task EditBranch(BranchRowViewModel? row)
    {
        if ((row ?? SelectedRow)?.Branch is not { } b) return;
        var otherNames = Branches.Where(r => r.Branch.Key != b.Key).Select(r => r.Branch.Name).ToList();
        var form = BranchFormViewModel.ForEdit(_session, b, otherNames);
        if (await _dialogs.ShowBranchFormAsync(form))
        {
            Info = $"Větev '{b.Key}' uložena.";
            await RunAsync(LoadAsync);
        }
    }

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedRow is not { } row) throw new InvalidOperationException("Vyber větev ze seznamu.");
        var b = row.Branch;
        // A branch with instances can't be plainly deleted (409). Offer the cascade up front instead.
        bool cascade = row.InstanceCount > 0;
        var message = cascade
            ? $"Větev '{b.Key}' má {row.InstanceCount} instancí. Smazat větev včetně všech instancí a jejich úloh?"
            : $"Opravdu smazat větev '{b.Key}'?";
        if (!await _dialogs.ConfirmAsync("Smazat větev", message))
            return;
        await _session.Require().DeleteBranchAsync(b.Key, cascade);
        Info = $"Větev '{b.Key}' smazána.";
        await LoadAsync();
    });

    /// <summary>Hromadné smazání všech zaškrtnutých větví. Pokračuje i když některá selže (např. má instance/úlohu).</summary>
    [RelayCommand]
    private Task DeleteSelectedAsync() => RunAsync(async () =>
    {
        var targets = Branches.Where(r => r.IsSelected).Select(r => r.Branch).ToList();
        if (targets.Count == 0) { Info = "Nevybral jsi žádnou větev."; return; }
        if (!await _dialogs.ConfirmAsync("Smazat větve", $"Opravdu smazat vybrané větve ({targets.Count})?"))
            return;

        var client = _session.Require();
        int deleted = 0;
        var failed = new List<string>();
        foreach (var b in targets)
        {
            try { await client.DeleteBranchAsync(b.Key); deleted++; }
            catch (DiffPdfApiException ex) { failed.Add($"{b.Key} ({ex.Detail ?? ex.StatusCode.ToString()})"); }
        }

        Info = failed.Count == 0
            ? $"Smazáno {deleted} větví."
            : $"Smazáno {deleted}, nešlo smazat {failed.Count}: {string.Join("; ", failed)}";
        await LoadAsync();
    });

    /// <summary>Hromadně povolí všechny zaškrtnuté větve.</summary>
    [RelayCommand]
    private Task EnableSelectedAsync() => SetSelectedEnabledAsync(true);

    /// <summary>Hromadně zakáže všechny zaškrtnuté větve.</summary>
    [RelayCommand]
    private Task DisableSelectedAsync() => SetSelectedEnabledAsync(false);

    private Task SetSelectedEnabledAsync(bool enabled) => RunAsync(async () =>
    {
        var targets = Branches.Where(r => r.IsSelected).Select(r => r.Branch).ToList();
        if (targets.Count == 0) { Info = "Nevybral jsi žádnou větev."; return; }

        var client = _session.Require();
        int done = 0;
        var failed = new List<string>();
        foreach (var b in targets)
        {
            try { await client.UpdateBranchAsync(b.Key, new UpdateBranchRequest(b.Name, enabled, b.Version)); done++; }
            catch (DiffPdfApiException ex) { failed.Add($"{b.Key} ({ex.Detail ?? ex.StatusCode.ToString()})"); }
        }

        Info = failed.Count == 0
            ? $"{(enabled ? "Povoleno" : "Zakázáno")}: {done} větví."
            : $"Hotovo {done}, selhalo {failed.Count}: {string.Join("; ", failed)}";
        await LoadAsync();
    });
}
