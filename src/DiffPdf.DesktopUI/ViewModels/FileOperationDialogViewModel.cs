using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

public enum FileOperationDialogMode { CreateFolder, Rename, Overwrite }

/// <summary>
/// Modal for the three small file-manager interactions: create folder, rename, and the per-file
/// overwrite question. Name validation mirrors the server's Windows rules for instant feedback —
/// the server stays authoritative (its 400/409 still surfaces if something slips through).
/// </summary>
public partial class FileOperationDialogViewModel : ObservableObject, IModalViewModel
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly bool _requirePdfExtension;

    private FileOperationDialogViewModel(
        FileOperationDialogMode mode, string windowTitle, string prompt, string confirmText,
        string initialName = "", bool requirePdfExtension = false)
    {
        Mode = mode;
        WindowTitle = windowTitle;
        Prompt = prompt;
        ConfirmText = confirmText;
        _name = initialName;
        _requirePdfExtension = requirePdfExtension;
    }

    public static FileOperationDialogViewModel ForCreateFolder() => new(
        FileOperationDialogMode.CreateFolder, "Nová složka", "Název nové složky:", "Vytvořit");

    public static FileOperationDialogViewModel ForRename(string currentName, bool isFile) => new(
        FileOperationDialogMode.Rename, "Přejmenovat", $"Nový název pro „{currentName}“:", "Přejmenovat",
        initialName: currentName, requirePdfExtension: isFile);

    public static FileOperationDialogViewModel ForOverwrite(string fileName, bool showApplyToAll = false) => new(
        FileOperationDialogMode.Overwrite, "Soubor už existuje",
        $"„{fileName}“ už v cílové složce existuje. Přepsat ho?", "Přepsat")
    {
        ShowApplyToAll = showApplyToAll,
    };

    public FileOperationDialogMode Mode { get; }
    public string WindowTitle { get; }
    public string Prompt { get; }
    public string ConfirmText { get; }
    public bool ShowNameInput => Mode is not FileOperationDialogMode.Overwrite;

    /// <summary>Show the "apply to all remaining conflicts" checkbox (batch overwrite question).</summary>
    public bool ShowApplyToAll { get; private init; }

    [ObservableProperty] private bool _applyToAll;

    /// <summary>True after the user confirmed (and, for name modes, the name validated).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The overwrite dialog's outcome as a batch decision (Confirmed × ApplyToAll).</summary>
    public OverwriteDecision Decision => (Confirmed, ApplyToAll) switch
    {
        (true, true) => OverwriteDecision.OverwriteAll,
        (true, false) => OverwriteDecision.Overwrite,
        (false, true) => OverwriteDecision.SkipAll,
        (false, false) => OverwriteDecision.Skip,
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string? _validationError;

    public bool HasValidationError => ValidationError is not null;

    public event Action? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (ShowNameInput)
        {
            ValidationError = ValidateName(Name.Trim());
            if (ValidationError is not null) return;
        }
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    private bool CanConfirm() => !ShowNameInput || !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    partial void OnNameChanged(string value) => ValidationError = null; // re-validate on confirm, not per keystroke

    /// <summary>The trimmed, validated name (call after the dialog confirmed).</summary>
    public string ResultName => Name.Trim();

    private string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Zadej název.";
        if (name.Length > 255) return "Název je příliš dlouhý (max. 255 znaků).";
        if (name is "." or "..") return "Tento název nelze použít.";
        if (name.IndexOfAny(InvalidNameChars) >= 0) return @"Název nesmí obsahovat \ / : * ? "" < > |.";
        if (name[^1] is '.' or ' ') return "Název nesmí končit tečkou ani mezerou.";

        int firstDot = name.IndexOf('.');
        string baseName = (firstDot < 0 ? name : name[..firstDot]).TrimEnd(' ');
        if (ReservedNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
            return "Tento název je ve Windows rezervovaný.";

        if (_requirePdfExtension && (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || name.Length <= 4))
            return "Soubor musí mít příponu .pdf.";
        return null;
    }
}
