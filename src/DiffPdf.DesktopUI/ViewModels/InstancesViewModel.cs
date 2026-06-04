using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Instance pod větví: seznam + vytvoření + úprava + smazání + detail se stavem (připravenost / struktura,
/// jen pro čtení) a statistikami dané instance. Akce „oprava struktury" sem nepatří — řeší ji sekce Automatizace.
/// </summary>
public partial class InstancesViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly NavigationService _navigation;

    public override string Title => "Instance";
    public override int NavOrder => 2;

    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<Instance> Instances { get; } = [];

    /// <summary>Statistiky vztažené pouze k vybrané instanci.</summary>
    public ObservableCollection<StatGroup> Stats { get; } = [];

    /// <summary>Spouštěče této instance (rychlý přehled + spuštění; správa je v sekci Spouštěče).</summary>
    public ObservableCollection<TriggerResponse> Triggers { get; } = [];

    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private bool _showCreateForm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBasePathPreview))]
    private string _newKey = string.Empty;

    [ObservableProperty] private string _newName = string.Empty;

    // Záloha pro případ, že server nemá nastavený kořen struktury: uživatel zadá kořen ručně a aplikace
    // k němu připojí \<větev>\<instanci> (konvence kořen/větev/instance/{old,new,reports}).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBasePathPreview))]
    private string _newRoot = string.Empty;

    // Kořen struktury nastavený na serveru (ScopeSync:RootPath). Je-li nastaven, cesty se odvozují z něj
    // a uživatel kořen vůbec nezadává.
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

    public InstancesViewModel(ServerSession session, DialogService dialogs, NavigationService navigation)
    {
        _session = session;
        _dialogs = dialogs;
        _navigation = navigation;
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
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
        Instances.Clear();
        if (SelectedBranch is null) return;
        foreach (var i in await _session.Require().ListInstancesAsync(SelectedBranch.Key)) Instances.Add(i);
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
        if (Instances.Any(i => string.Equals(i.Key, key, StringComparison.Ordinal)))
            return $"Instance s klíčem '{key}' už v této větvi existuje.";
        if (Instances.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
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

    // Výběr instance načte (jen pro čtení) její připravenost a inspekci struktury old/new/reports a statistiky.
    // Žádný zápis na disk — oprava struktury se řeší přes sekci Automatizace, ne odsud.
    partial void OnSelectedInstanceChanged(Instance? value)
    {
        Stats.Clear();
        Triggers.Clear();
        if (value is null || SelectedBranch is null)
        {
            Readiness = null;
            Structure = null;
            return;
        }
        EditName = value.Name;
        EditBasePath = value.BasePath;
        EditCredentialProfile = value.CredentialProfile ?? string.Empty;
        EditEnabled = value.Enabled;
        _ = RunAsync(LoadInstanceDetailsAsync);
    }

    private async Task LoadInstanceDetailsAsync()
    {
        if (SelectedBranch is null || SelectedInstance is null)
            return;
        var client = _session.Require();
        var readiness = await client.GetReadinessAsync(SelectedBranch.Key, SelectedInstance.Key, sampleSize: 10);
        Readiness = readiness;
        Structure = readiness.Structure; // read-only inspection (Present / Missing / WrongType)

        var stats = await client.GetInstanceStatsAsync(SelectedBranch.Key, SelectedInstance.Key);
        Stats.Clear();
        foreach (var g in ScopeStatGroups.From(stats)) Stats.Add(g);

        Triggers.Clear();
        foreach (var t in await client.ListInstanceTriggersAsync(SelectedInstance.Id)) Triggers.Add(t);
    }

    /// <summary>Spustí vybraný spouštěč této instance a přeskočí na úlohu (správa spouštěčů je v sekci Spouštěče).</summary>
    [RelayCommand]
    private Task RunTriggerAsync(TriggerResponse? trigger) => RunAsync(async () =>
    {
        if (trigger is null) return;
        var result = await _session.Require().RunTriggerAsync(trigger.Id);
        Info = result.Success
            ? $"Spouštěč '{trigger.Name}' spuštěn: {result.Status}" + (result.BatchJobId is { } id ? $" (úloha {id})" : "")
            : $"Spouštěč '{trigger.Name}' nespuštěn: {result.Message}";
        if (result.BatchJobId is { } jobId)
            _navigation.GoTo<JobsViewModel>(j => j.OpenJob(jobId));
    });

    /// <summary>Přejde do sekce Spouštěče předfiltrované na tuto instanci.</summary>
    [RelayCommand]
    private void ManageTriggers()
    {
        if (SelectedBranch is null || SelectedInstance is null) return;
        var branchKey = SelectedBranch.Key;
        var instanceKey = SelectedInstance.Key;
        _navigation.GoTo<TriggerManagementViewModel>(vm => vm.FocusInstance(branchKey, instanceKey));
    }
}
