using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Větve: seznam + vytvoření + úprava + smazání + detail se statistikami, a u každého řádku ⚙ konfigurace
/// a tlačítka fronty (Spustit / Přidat do fronty / Pozastavit / Zastavit / Obnovit) řízená živým stavem fronty.
/// </summary>
public partial class BranchesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly JobProgressHubClient _hub;
    private bool _subscribed;

    public override string Title => "Větve";
    public override int NavOrder => 1;

    public ObservableCollection<BranchRowViewModel> Branches { get; } = [];

    /// <summary>Statistiky vztažené pouze k vybrané větvi (úlohy, kontroly, automatizace, porovnávání).</summary>
    public ObservableCollection<StatGroup> Stats { get; } = [];

    /// <summary>Spouštěče v této větvi (souhrn).</summary>
    public ObservableCollection<TriggerResponse> Triggers { get; } = [];

    [ObservableProperty] private BranchRowViewModel? _selectedRow;
    [ObservableProperty] private bool _showCreateForm;

    /// <summary>Vybraná větev (odvozená z vybraného řádku) — pro detailní panel a akce úprav.</summary>
    public Branch? SelectedBranch => SelectedRow?.Branch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchFolderPreview))]
    private string _newKey = string.Empty;

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _info;

    // Kořen struktury nastavený na serveru (ScopeSync:RootPath); je-li nastaven, vytvoření větve založí složku 'kořen\větev'.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchFolderPreview))]
    private string? _scopeRoot;

    /// <summary>Náhled složky, kterou vytvoření větve založí (jen když má server nastavený kořen struktury).</summary>
    public string BranchFolderPreview
    {
        get
        {
            string key = NewKey.Trim();
            if (string.IsNullOrWhiteSpace(ScopeRoot) || key.Length == 0) return string.Empty;
            string root = ScopeRoot!.Trim().TrimEnd('\\', '/');
            return root.StartsWith(@"\\", StringComparison.Ordinal) ? $@"{root}\{key}" : System.IO.Path.Combine(root, key);
        }
    }

    // Editace vybrané větve (klíč je neměnná identita).
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private bool _editEnabled = true;

    public BranchesViewModel(ServerSession session, DialogService dialogs, JobProgressHubClient hub)
    {
        _session = session;
        _dialogs = dialogs;
        _hub = hub;
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        try { await _hub.EnsureStartedAsync(); } catch { /* live queue state is best-effort */ }
        if (!_subscribed) { _hub.QueueStateReceived += OnQueueState; _subscribed = true; }

        await LoadAsync();
        ScopeRoot = (await _session.Require().GetScopeRootAsync()).Root;
    });

    private async Task LoadAsync()
    {
        var client = _session.Require();
        var list = await client.ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(new BranchRowViewModel(b));

        // Seed each row's queue state and join its SignalR group for live pushes.
        foreach (var row in Branches)
        {
            try
            {
                await _hub.JoinBranchAsync(row.Branch.Key);
                row.Apply(await client.GetBranchQueueAsync(row.Branch.Key));
            }
            catch { /* queue state is best-effort */ }
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

    partial void OnSelectedRowChanged(BranchRowViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedBranch));
        Stats.Clear();
        Triggers.Clear();
        if (value is null) return;
        EditName = value.Branch.Name;
        EditEnabled = value.Branch.Enabled;
        _ = RunAsync(LoadStatsAsync);
    }

    private async Task LoadStatsAsync()
    {
        Stats.Clear();
        Triggers.Clear();
        if (SelectedBranch is not { } b) return;
        var client = _session.Require();
        var stats = await client.GetBranchStatsAsync(b.Key);
        foreach (var g in ScopeStatGroups.From(stats)) Stats.Add(g);
        foreach (var t in await client.ListBranchTriggersAsync(b.Id)) Triggers.Add(t);
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

    [RelayCommand]
    private void ShowCreate()
    {
        NewKey = string.Empty;
        NewName = string.Empty;
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

        await _session.Require().CreateBranchAsync(new CreateBranchRequest(NewKey.Trim(), NewName.Trim()));
        Info = $"Větev '{NewKey.Trim()}' vytvořena.";
        ShowCreateForm = false;
        await LoadAsync();
    });

    /// <summary>Klientská kontrola: klíč + název povinné a oba unikátní (klíč je case-sensitive jako na serveru).</summary>
    private string? Validate()
    {
        var key = NewKey.Trim();
        var name = NewName.Trim();
        if (key.Length == 0) return "Zadej klíč.";
        if (name.Length == 0) return "Zadej název.";
        if (Branches.Any(r => string.Equals(r.Branch.Key, key, StringComparison.Ordinal)))
            return $"Větev s klíčem '{key}' už existuje.";
        if (Branches.Any(r => string.Equals(r.Branch.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"Větev s názvem '{name}' už existuje.";
        return null;
    }

    [RelayCommand]
    private Task SaveEditAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is not { } b) throw new InvalidOperationException("Vyber větev ze seznamu.");
        await _session.Require().UpdateBranchAsync(b.Key, new UpdateBranchRequest(EditName.Trim(), EditEnabled, b.Version));
        Info = $"Větev '{b.Key}' uložena.";
        await LoadAsync();
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is not { } b) throw new InvalidOperationException("Vyber větev ze seznamu.");
        if (!await _dialogs.ConfirmAsync("Smazat větev", $"Opravdu smazat větev '{b.Key}'?"))
            return;
        await _session.Require().DeleteBranchAsync(b.Key);
        Info = $"Větev '{b.Key}' smazána.";
        await LoadAsync();
    });
}
