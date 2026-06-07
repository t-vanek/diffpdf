using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Instance pod větví: seznam + vytvoření + úprava + smazání + detail (připravenost / struktura, jen pro
/// čtení) a statistiky. U každého řádku ⚙ konfigurace a tlačítka fronty (Spustit / Přidat do fronty /
/// Pozastavit / Zastavit / Obnovit) řízená živým stavem per-branch fronty. Instance ve větvi běží sekvenčně.
/// </summary>
public partial class InstancesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly JobProgressHubClient _hub;
    private bool _subscribed;

    public override string Title => "Instance";
    public override int NavOrder => 2;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<InstanceRowViewModel> Instances { get; } = [];

    /// <summary>Statistiky vztažené pouze k vybrané instanci.</summary>
    public ObservableCollection<StatGroup> Stats { get; } = [];

    /// <summary>Spouštěče této instance (souhrn; konfigurace je přes ⚙).</summary>
    public ObservableCollection<TriggerResponse> Triggers { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private InstanceRowViewModel? _selectedRow;
    [ObservableProperty] private bool _showCreateForm;

    /// <summary>Vybraná instance (odvozená z vybraného řádku) — pro detailní panel a akce úprav.</summary>
    public Instance? SelectedInstance => SelectedRow?.Instance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBasePathPreview))]
    private string _newKey = string.Empty;

    [ObservableProperty] private string _newName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBasePathPreview))]
    private string _newRoot = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBasePathPreview))]
    [NotifyPropertyChangedFor(nameof(RootInputVisible))]
    private string? _scopeRoot;

    [ObservableProperty] private string _newCredentialProfile = string.Empty;
    [ObservableProperty] private bool _ensureStructure = true;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private InstanceStructureReport? _structure;
    [ObservableProperty] private InstanceReadiness? _readiness;

    // Editace vybrané instance (klíč je neměnná identita).
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editBasePath = string.Empty;
    [ObservableProperty] private string _editCredentialProfile = string.Empty;
    [ObservableProperty] private bool _editEnabled = true;

    /// <summary>Pole pro ruční kořen se zobrazí jen tehdy, když server kořen struktury nemá nastavený.</summary>
    public bool RootInputVisible => string.IsNullOrWhiteSpace(ScopeRoot);

    private string EffectiveRoot => string.IsNullOrWhiteSpace(ScopeRoot) ? NewRoot.Trim() : ScopeRoot!.Trim();

    /// <summary>Výsledná základní cesta instance: kořen + větev + klíč instance (živý náhled pro zakládací formulář).</summary>
    public string NewBasePathPreview
    {
        get
        {
            string root = EffectiveRoot;
            string key = NewKey.Trim();
            if (root.Length == 0 || SelectedBranch is null || key.Length == 0)
                return string.Empty;
            return Combine(Combine(root, SelectedBranch.Key), key);
        }
    }

    /// <summary>Spojí cestu se složkou; pro UNC kořen zachová zpětná lomítka (stejně jako server).</summary>
    private static string Combine(string root, string sub)
    {
        string trimmed = root.TrimEnd('\\', '/');
        return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ? $@"{trimmed}\{sub}" : System.IO.Path.Combine(trimmed, sub);
    }

    public InstancesViewModel(ServerSession session, DialogService dialogs, JobProgressHubClient hub)
    {
        _session = session;
        _dialogs = dialogs;
        _hub = hub;
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        try { await _hub.EnsureStartedAsync(); } catch { /* live queue state is best-effort */ }
        if (!_subscribed) { _hub.QueueStateReceived += OnQueueState; _subscribed = true; }

        await LoadBranchesAsync();
        ScopeRoot = (await _session.Require().GetScopeRootAsync()).Root;
    });

    private async Task LoadBranchesAsync()
    {
        var list = await _session.Require().ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(b);
    }

    private async Task LoadInstancesAsync()
    {
        if (SelectedBranch is not { } branch)
        {
            Instances.Clear();
            return;
        }
        var client = _session.Require();

        // Fetch first, then rebuild. Clearing up front and appending after the await is NOT safe against
        // overlapping invocations (branch change / refresh / page re-activate): two loads would each clear
        // while empty and then each append the full list, leaving every instance listed twice. Building the
        // list, then doing Clear()+Add() with no await between them, keeps the rebuild atomic on the UI thread.
        var loaded = await client.ListInstancesAsync(branch.Key);

        // A newer load (different branch, or a refresh) started while we awaited → let it own the grid.
        if (SelectedBranch?.Key != branch.Key)
            return;

        Instances.Clear();
        foreach (var i in loaded)
            Instances.Add(new InstanceRowViewModel(i));

        // Seed per-instance run-queue status and join the branch's SignalR group for live pushes.
        try
        {
            await _hub.JoinBranchAsync(branch.Key);
            if (SelectedBranch?.Key == branch.Key)
                ApplyState(await client.GetBranchQueueAsync(branch.Key));
        }
        catch { /* queue state is best-effort */ }
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

    partial void OnSelectedBranchChanged(Branch? value)
    {
        OnPropertyChanged(nameof(NewBasePathPreview));
        _ = RunAsync(LoadInstancesAsync);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadBranchesAsync();
        await LoadInstancesAsync();
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

    [RelayCommand]
    private void ShowCreate()
    {
        NewKey = NewName = NewRoot = NewCredentialProfile = string.Empty;
        EnsureStructure = true;
        ValidationError = null;
        Info = null;
        ShowCreateForm = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        ShowCreateForm = false;
        ValidationError = null;
    }

    [RelayCommand]
    private Task SaveCreateAsync() => RunAsync(async () =>
    {
        ValidationError = Validate();
        if (ValidationError is not null) return;

        await _session.Require().CreateInstanceAsync(
            SelectedBranch!.Key,
            new CreateInstanceRequest(NewKey.Trim(), NewName.Trim(), NewBasePathPreview,
                string.IsNullOrWhiteSpace(NewCredentialProfile) ? null : NewCredentialProfile.Trim()),
            EnsureStructure);
        Info = $"Instance '{NewKey.Trim()}' vytvořena v {NewBasePathPreview}.";
        ShowCreateForm = false;
        await LoadInstancesAsync();
    });

    /// <summary>Klientská kontrola: klíč + název + cesta povinné, klíč/název unikátní v rámci větve.</summary>
    private string? Validate()
    {
        if (SelectedBranch is null) return "Vyber větev.";
        var key = NewKey.Trim();
        var name = NewName.Trim();
        if (key.Length == 0) return "Zadej klíč.";
        if (name.Length == 0) return "Zadej název.";
        if (RootInputVisible && NewRoot.Trim().Length == 0) return "Zadej kořenovou složku.";
        if (Instances.Any(r => string.Equals(r.Instance.Key, key, StringComparison.Ordinal)))
            return $"Instance s klíčem '{key}' už v této větvi existuje.";
        if (Instances.Any(r => string.Equals(r.Instance.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"Instance s názvem '{name}' už v této větvi existuje.";
        return null;
    }

    [RelayCommand]
    private Task SaveEditAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null || SelectedInstance is not { } inst)
            throw new InvalidOperationException("Vyber instanci v seznamu.");
        if (string.IsNullOrWhiteSpace(EditBasePath))
        {
            ValidationError = "Zadej základní cestu.";
            return;
        }
        await _session.Require().UpdateInstanceAsync(SelectedBranch.Key, inst.Key, new UpdateInstanceRequest(
            EditName.Trim(), EditBasePath.Trim(),
            string.IsNullOrWhiteSpace(EditCredentialProfile) ? null : EditCredentialProfile.Trim(),
            EditEnabled, inst.Version));
        Info = $"Instance '{inst.Key}' uložena.";
        await LoadInstancesAsync();
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null || SelectedInstance is null)
            throw new InvalidOperationException("Vyber větev i instanci.");
        var key = SelectedInstance.Key;
        if (!await _dialogs.ConfirmAsync("Smazat instanci", $"Opravdu smazat instanci '{key}'?"))
            return;
        await _session.Require().DeleteInstanceAsync(SelectedBranch.Key, key);
        Info = $"Instance '{key}' smazána.";
        await LoadInstancesAsync();
    });

    // Výběr instance načte (jen pro čtení) připravenost, inspekci struktury old/new/reports a statistiky.
    partial void OnSelectedRowChanged(InstanceRowViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedInstance));
        Stats.Clear();
        Triggers.Clear();
        if (value is null || SelectedBranch is null)
        {
            Readiness = null;
            Structure = null;
            return;
        }
        EditName = value.Instance.Name;
        EditBasePath = value.Instance.BasePath;
        EditCredentialProfile = value.Instance.CredentialProfile ?? string.Empty;
        EditEnabled = value.Instance.Enabled;
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
        var triggers = await client.ListInstanceTriggersAsync(instance.Id);

        // Selection moved on while we loaded → discard these now-stale results; the current selection's own
        // load populates the panel. (OnSelectedRowChanged already cleared Stats/Triggers on the change.)
        if (SelectedInstance is not { } current || current.Key != instance.Key || SelectedBranch?.Key != branch.Key)
            return;

        Readiness = readiness;
        Structure = readiness.Structure; // read-only inspection (Present / Missing / WrongType)
        Stats.Clear();
        foreach (var g in ScopeStatGroups.From(stats)) Stats.Add(g);
        Triggers.Clear();
        foreach (var t in triggers) Triggers.Add(t);
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
