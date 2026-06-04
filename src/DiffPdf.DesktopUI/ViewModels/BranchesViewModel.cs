using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Větve: seznam + vytvoření + úprava (název / povoleno) + smazání + detail se stavem a statistikami dané větve.</summary>
public partial class BranchesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public override string Title => "Větve";
    public override int NavOrder => 1;

    public ObservableCollection<Branch> Branches { get; } = [];

    /// <summary>Statistiky vztažené pouze k vybrané větvi (úlohy, kontroly, automatizace, porovnávání).</summary>
    public ObservableCollection<StatGroup> Stats { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private bool _showCreateForm;

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

    public BranchesViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        await LoadAsync();
        ScopeRoot = (await _session.Require().GetScopeRootAsync()).Root;
    });

    private async Task LoadAsync()
    {
        var list = await _session.Require().ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(b);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);

    partial void OnSelectedBranchChanged(Branch? value)
    {
        Stats.Clear();
        if (value is null) return;
        EditName = value.Name;
        EditEnabled = value.Enabled;
        _ = RunAsync(LoadStatsAsync);
    }

    private async Task LoadStatsAsync()
    {
        Stats.Clear();
        if (SelectedBranch is not { } b) return;
        var stats = await _session.Require().GetBranchStatsAsync(b.Key);
        foreach (var g in ScopeStatGroups.From(stats)) Stats.Add(g);
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
        if (Branches.Any(b => string.Equals(b.Key, key, StringComparison.Ordinal)))
            return $"Větev s klíčem '{key}' už existuje.";
        if (Branches.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
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
        if (SelectedBranch is null) throw new InvalidOperationException("Vyber větev ze seznamu.");
        var key = SelectedBranch.Key;
        if (!await _dialogs.ConfirmAsync("Smazat větev", $"Opravdu smazat větev '{key}'?"))
            return;
        await _session.Require().DeleteBranchAsync(key);
        Info = $"Větev '{key}' smazána.";
        await LoadAsync();
    });
}
