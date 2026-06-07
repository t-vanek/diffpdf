using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Modal form for creating or editing a branch. Self-contained: it validates the input (required + unique),
/// performs the create/update call, and on success sets <see cref="Saved"/> and raises
/// <see cref="CloseRequested"/>. The key is the branch's immutable identity, so it is editable only when
/// creating; <c>Enabled</c> is only meaningful when editing.
/// </summary>
public partial class BranchFormViewModel : ViewModelBase
{
    private readonly ServerSession _session;
    private readonly Branch? _editing;                       // null ⇒ create mode
    private readonly IReadOnlyCollection<string> _existingKeys;
    private readonly IReadOnlyCollection<string> _otherNames; // names to check uniqueness against
    private readonly string? _scopeRoot;

    public bool IsEditMode => _editing is not null;
    public string WindowTitle => IsEditMode ? "Úprava větve" : "Nová větev";
    public string SaveButtonText => IsEditMode ? "Uložit změny" : "Vytvořit";

    /// <summary>True once the create/update succeeded — the dialog returns this to its caller.</summary>
    public bool Saved { get; private set; }

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderPreview))]
    private string _key = string.Empty;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string? _validationError;

    private BranchFormViewModel(ServerSession session, Branch? editing, string? scopeRoot,
        IReadOnlyCollection<string> existingKeys, IReadOnlyCollection<string> otherNames)
    {
        _session = session;
        _editing = editing;
        _scopeRoot = scopeRoot;
        _existingKeys = existingKeys;
        _otherNames = otherNames;
        if (editing is not null)
        {
            Key = editing.Key;
            Name = editing.Name;
            Enabled = editing.Enabled;
        }
    }

    /// <param name="existingNames">All existing branch names (for the uniqueness check).</param>
    public static BranchFormViewModel ForCreate(ServerSession session, string? scopeRoot,
        IReadOnlyCollection<string> existingKeys, IReadOnlyCollection<string> existingNames) =>
        new(session, editing: null, scopeRoot, existingKeys, existingNames);

    /// <param name="otherNames">Names of the <i>other</i> branches (so keeping the same name is allowed).</param>
    public static BranchFormViewModel ForEdit(ServerSession session, Branch branch, IReadOnlyCollection<string> otherNames) =>
        new(session, branch, scopeRoot: null, existingKeys: [], otherNames);

    /// <summary>Folder the branch will create (create mode only, and only when the server has a structure root).</summary>
    public string FolderPreview
    {
        get
        {
            if (IsEditMode) return string.Empty;
            var key = Key.Trim();
            if (string.IsNullOrWhiteSpace(_scopeRoot) || key.Length == 0) return string.Empty;
            var root = _scopeRoot!.Trim().TrimEnd('\\', '/');
            return root.StartsWith(@"\\", StringComparison.Ordinal) ? $@"{root}\{key}" : System.IO.Path.Combine(root, key);
        }
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        ValidationError = Validate();
        if (ValidationError is not null) return;

        if (_editing is { } b)
            await _session.Require().UpdateBranchAsync(b.Key, new UpdateBranchRequest(Name.Trim(), Enabled, b.Version));
        else
            await _session.Require().CreateBranchAsync(new CreateBranchRequest(Key.Trim(), Name.Trim()));

        Saved = true;
        CloseRequested?.Invoke();
    });

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>Klientská kontrola: povinná pole + unikátnost (klíč case-sensitive jako na serveru). Null = OK.</summary>
    private string? Validate()
    {
        var name = Name.Trim();
        if (!IsEditMode)
        {
            var key = Key.Trim();
            if (key.Length == 0) return "Zadej klíč.";
            if (!ScopeKey.IsValid(key)) return ScopeKey.Rule;
            if (_existingKeys.Any(k => string.Equals(k, key, StringComparison.Ordinal)))
                return $"Větev s klíčem '{key}' už existuje.";
        }
        if (name.Length == 0) return "Zadej název.";
        if (_otherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"Větev s názvem '{name}' už existuje.";
        return null;
    }
}
