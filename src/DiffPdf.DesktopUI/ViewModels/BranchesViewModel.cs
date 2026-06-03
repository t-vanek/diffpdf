using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Branches: list + create (reveal form with client-side uniqueness validation) + delete (confirmed).</summary>
public partial class BranchesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public override string Title => "Branches";
    public override int NavOrder => 1;

    public ObservableCollection<Branch> Branches { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private bool _showCreateForm;
    [ObservableProperty] private string _newKey = string.Empty;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _info;

    public BranchesViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        var list = await _session.Require().ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(b);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);

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
        Info = $"Branch '{NewKey.Trim()}' vytvořen.";
        ShowCreateForm = false;
        await LoadAsync();
    });

    /// <summary>Client-side guard: key + name required and both unique (key is case-sensitive like the server).</summary>
    private string? Validate()
    {
        var key = NewKey.Trim();
        var name = NewName.Trim();
        if (key.Length == 0) return "Zadej klíč.";
        if (name.Length == 0) return "Zadej název.";
        if (Branches.Any(b => string.Equals(b.Key, key, StringComparison.Ordinal)))
            return $"Branch s klíčem '{key}' už existuje.";
        if (Branches.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"Branch s názvem '{name}' už existuje.";
        return null;
    }

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null) throw new InvalidOperationException("Vyber branch ze seznamu.");
        var key = SelectedBranch.Key;
        if (!await _dialogs.ConfirmAsync("Smazat branch", $"Opravdu smazat branch '{key}'?"))
            return;
        await _session.Require().DeleteBranchAsync(key);
        Info = $"Branch '{key}' smazán.";
        await LoadAsync();
    });
}
