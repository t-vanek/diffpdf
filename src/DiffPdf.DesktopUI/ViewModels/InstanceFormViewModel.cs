using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Modal form for creating or editing an instance under a branch. Self-contained: it validates the input
/// (required + unique within the branch), performs the create/update call, and on success sets
/// <see cref="Saved"/> and raises <see cref="CloseRequested"/>. The key is the immutable identity, so it is
/// editable only when creating. When creating, the base path is derived (root + branch + key) and the
/// old/new/reports skeleton can be provisioned; when editing, the base path is edited directly.
/// </summary>
public partial class InstanceFormViewModel : ViewModelBase
{
    private readonly ServerSession _session;
    private readonly string _branchKey;
    private readonly Instance? _editing;                       // null ⇒ create mode
    private readonly IReadOnlyCollection<string> _existingKeys;
    private readonly IReadOnlyCollection<string> _otherNames;
    private readonly string? _scopeRoot;

    public bool IsEditMode => _editing is not null;
    public bool IsCreateMode => _editing is null;
    public string WindowTitle => IsEditMode ? "Úprava instance" : "Nová instance";
    public string SaveButtonText => IsEditMode ? "Uložit změny" : "Vytvořit";

    /// <summary>True once the create/update succeeded — the dialog returns this to its caller.</summary>
    public bool Saved { get; private set; }

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BasePathPreview))]
    private string _key = string.Empty;

    [ObservableProperty] private string _name = string.Empty;

    // Create mode: manual root (only when the server has no structure root) → feeds BasePathPreview.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BasePathPreview))]
    private string _root = string.Empty;

    // Edit mode: the instance's base path, edited directly.
    [ObservableProperty] private string _basePath = string.Empty;

    [ObservableProperty] private string _credentialProfile = string.Empty;
    [ObservableProperty] private bool _ensureStructure = true;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string? _validationError;

    /// <summary>Create mode only: the manual-root box shows when the server has no configured structure root.</summary>
    public bool RootInputVisible => IsCreateMode && string.IsNullOrWhiteSpace(_scopeRoot);

    private InstanceFormViewModel(ServerSession session, string branchKey, Instance? editing, string? scopeRoot,
        IReadOnlyCollection<string> existingKeys, IReadOnlyCollection<string> otherNames)
    {
        _session = session;
        _branchKey = branchKey;
        _editing = editing;
        _scopeRoot = scopeRoot;
        _existingKeys = existingKeys;
        _otherNames = otherNames;
        if (editing is not null)
        {
            Key = editing.Key;
            Name = editing.Name;
            BasePath = editing.BasePath;
            CredentialProfile = editing.CredentialProfile ?? string.Empty;
            Enabled = editing.Enabled;
        }
    }

    public static InstanceFormViewModel ForCreate(ServerSession session, string branchKey, string? scopeRoot,
        IReadOnlyCollection<string> existingKeys, IReadOnlyCollection<string> existingNames) =>
        new(session, branchKey, editing: null, scopeRoot, existingKeys, existingNames);

    public static InstanceFormViewModel ForEdit(ServerSession session, string branchKey, Instance instance,
        IReadOnlyCollection<string> otherNames) =>
        new(session, branchKey, instance, scopeRoot: null, existingKeys: [], otherNames);

    private string EffectiveRoot => string.IsNullOrWhiteSpace(_scopeRoot) ? Root.Trim() : _scopeRoot!.Trim();

    /// <summary>Create mode: derived base path (root + branch + key) shown as a live preview.</summary>
    public string BasePathPreview
    {
        get
        {
            if (IsEditMode) return string.Empty;
            var root = EffectiveRoot;
            var key = Key.Trim();
            if (root.Length == 0 || key.Length == 0) return string.Empty;
            return Combine(Combine(root, _branchKey), key);
        }
    }

    /// <summary>Joins a path with a subfolder; for a UNC root keeps backslashes (same as the server).</summary>
    private static string Combine(string root, string sub)
    {
        var trimmed = root.TrimEnd('\\', '/');
        return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ? $@"{trimmed}\{sub}" : System.IO.Path.Combine(trimmed, sub);
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        ValidationError = Validate();
        if (ValidationError is not null) return;

        var profile = string.IsNullOrWhiteSpace(CredentialProfile) ? null : CredentialProfile.Trim();
        if (_editing is { } inst)
            await _session.Require().UpdateInstanceAsync(_branchKey, inst.Key,
                new UpdateInstanceRequest(Name.Trim(), BasePath.Trim(), profile, Enabled, inst.Version));
        else
            await _session.Require().CreateInstanceAsync(_branchKey,
                new CreateInstanceRequest(Key.Trim(), Name.Trim(), BasePathPreview, profile), EnsureStructure);

        Saved = true;
        CloseRequested?.Invoke();
    });

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>Klientská kontrola: povinná pole + unikátnost v rámci větve (klíč case-sensitive). Null = OK.</summary>
    private string? Validate()
    {
        var name = Name.Trim();
        if (IsCreateMode)
        {
            var key = Key.Trim();
            if (key.Length == 0) return "Zadej klíč.";
            if (!ScopeKey.IsValid(key)) return ScopeKey.Rule;
            if (_existingKeys.Any(k => string.Equals(k, key, StringComparison.Ordinal)))
                return $"Instance s klíčem '{key}' už v této větvi existuje.";
        }
        if (name.Length == 0) return "Zadej název.";
        if (_otherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"Instance s názvem '{name}' už v této větvi existuje.";

        if (IsCreateMode)
        {
            if (RootInputVisible && Root.Trim().Length == 0) return "Zadej kořenovou složku.";
        }
        else if (BasePath.Trim().Length == 0)
        {
            return "Zadej základní cestu.";
        }
        return null;
    }
}
