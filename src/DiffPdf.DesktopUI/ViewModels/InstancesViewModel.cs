using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Instances under a branch: list + create (reveal form + uniqueness validation) + delete (confirmed); inspect structure / readiness.</summary>
public partial class InstancesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public override string Title => "Instances";
    public override int NavOrder => 2;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<Instance> Instances { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private bool _showCreateForm;
    [ObservableProperty] private string _newKey = string.Empty;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newBasePath = string.Empty;
    [ObservableProperty] private string _newCredentialProfile = string.Empty;
    [ObservableProperty] private bool _ensureStructure = true;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private InstanceStructureReport? _structure;
    [ObservableProperty] private InstanceReadiness? _readiness;

    public InstancesViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public override Task ActivateAsync() => RunAsync(LoadBranchesAsync);

    private async Task LoadBranchesAsync()
    {
        var list = await _session.Require().ListBranchesAsync();
        Branches.Clear();
        foreach (var b in list) Branches.Add(b);
    }

    private async Task LoadInstancesAsync()
    {
        Instances.Clear();
        if (SelectedBranch is null) return;
        foreach (var i in await _session.Require().ListInstancesAsync(SelectedBranch.Key)) Instances.Add(i);
    }

    partial void OnSelectedBranchChanged(Branch? value) => _ = RunAsync(LoadInstancesAsync);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadBranchesAsync();
        await LoadInstancesAsync();
    });

    [RelayCommand]
    private void ShowCreate()
    {
        NewKey = NewName = NewBasePath = NewCredentialProfile = string.Empty;
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
            new CreateInstanceRequest(NewKey.Trim(), NewName.Trim(), NewBasePath.Trim(),
                string.IsNullOrWhiteSpace(NewCredentialProfile) ? null : NewCredentialProfile.Trim()),
            EnsureStructure);
        Info = $"Instance '{NewKey.Trim()}' vytvořena.";
        ShowCreateForm = false;
        await LoadInstancesAsync();
    });

    /// <summary>Client-side guard: key + name + basePath required, key/name unique within the branch.</summary>
    private string? Validate()
    {
        if (SelectedBranch is null) return "Vyber branch.";
        var key = NewKey.Trim();
        var name = NewName.Trim();
        var basePath = NewBasePath.Trim();
        if (key.Length == 0) return "Zadej klíč.";
        if (name.Length == 0) return "Zadej název.";
        if (basePath.Length == 0) return "Zadej BasePath.";
        if (Instances.Any(i => string.Equals(i.Key, key, StringComparison.Ordinal)))
            return $"Instance s klíčem '{key}' už v této větvi existuje.";
        if (Instances.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"Instance s názvem '{name}' už v této větvi existuje.";
        return null;
    }

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null || SelectedInstance is null)
            throw new InvalidOperationException("Vyber branch i instanci.");
        var key = SelectedInstance.Key;
        if (!await _dialogs.ConfirmAsync("Smazat instanci", $"Opravdu smazat instanci '{key}'?"))
            return;
        await _session.Require().DeleteInstanceAsync(SelectedBranch.Key, key);
        Info = $"Instance '{key}' smazána.";
        await LoadInstancesAsync();
    });

    // Selecting an instance in the list loads its readiness (read-only) and, from the same response,
    // the inspected old/new/reports structure — no button and no disk writes (unlike the create/repair
    // "ensure", which stays out of a plain selection).
    partial void OnSelectedInstanceChanged(Instance? value)
    {
        if (value is null || SelectedBranch is null)
        {
            Readiness = null;
            Structure = null;
            return;
        }
        _ = RunAsync(LoadInstanceDetailsAsync);
    }

    private async Task LoadInstanceDetailsAsync()
    {
        if (SelectedBranch is null || SelectedInstance is null)
            return;
        var readiness = await _session.Require().GetReadinessAsync(SelectedBranch.Key, SelectedInstance.Key, sampleSize: 10);
        Readiness = readiness;
        Structure = readiness.Structure; // read-only inspection (Present / Missing / WrongType)
    }

    /// <summary>Explicit create/repair of the selected instance's old/new/reports structure (writes to
    /// disk: creates missing folders, replaces a file occupying a folder name), then refreshes the view.</summary>
    [RelayCommand]
    private Task RepairStructureAsync() => RunAsync(async () =>
    {
        if (SelectedBranch is null || SelectedInstance is null)
            throw new InvalidOperationException("Vyber instanci v seznamu.");
        if (!await _dialogs.ConfirmAsync("Opravit strukturu",
                $"Vytvoří chybějící old/new/reports pro '{SelectedInstance.Key}' a nahradí soubor kolidující s názvem složky. Pokračovat?"))
            return;
        Structure = await _session.Require().EnsureStructureAsync(SelectedBranch.Key, SelectedInstance.Key);
        Readiness = await _session.Require().GetReadinessAsync(SelectedBranch.Key, SelectedInstance.Key, sampleSize: 10);
    });
}
