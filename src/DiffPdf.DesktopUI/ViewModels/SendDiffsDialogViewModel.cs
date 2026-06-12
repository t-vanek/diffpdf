using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Modal form for e-mailing a job's highlighted diff PDFs — either every differing pair of the batch
/// (<c>files</c> = null) or an explicit pair (the file-detail window's "Odeslat e-mailem"). Self-contained like
/// the other forms: validates the recipients, performs the send and on success sets <see cref="Result"/> and
/// raises <see cref="CloseRequested"/>. The last used recipients are remembered in the client settings, so the
/// next send (typically to the same print operator) is pre-filled.
/// </summary>
public partial class SendDiffsDialogViewModel : ViewModelBase, IModalViewModel
{
    private readonly ServerSession _session;
    private readonly ClientSettingsStore _settings;
    private readonly Guid _jobId;
    private readonly IReadOnlyList<string>? _files; // null ⇒ every differing pair of the batch

    public string WindowTitle => "Odeslat odlišné PDF e-mailem";

    /// <summary>What is being sent — shown in the dialog so the scope (one file vs. whole batch) is unmistakable.</summary>
    public string ScopeDescription { get; }

    /// <summary>The server's send outcome once it succeeded — the caller turns it into a toast.</summary>
    public SendJobDiffsResponse? Result { get; private set; }

    public event Action? CloseRequested;

    [ObservableProperty] private string _recipients = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string? _validationError;

    public SendDiffsDialogViewModel(
        ServerSession session, Guid jobId, string scopeDescription, IReadOnlyList<string>? files,
        ClientSettingsStore? settings = null)
    {
        _session = session;
        _jobId = jobId;
        _files = files;
        _settings = settings ?? new ClientSettingsStore();
        ScopeDescription = scopeDescription;
        Recipients = _settings.LoadSendRecipients() ?? string.Empty;
    }

    /// <summary>Splits a recipients line on commas/semicolons/whitespace into the individual addresses.</summary>
    public static IReadOnlyList<string> ParseRecipients(string recipients) =>
        recipients.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand]
    private Task SendAsync() => RunAsync(async () =>
    {
        var parsed = ParseRecipients(Recipients);
        ValidationError = parsed.Count == 0 ? "Zadej alespoň jednoho příjemce."
            : parsed.FirstOrDefault(r => !r.Contains('@')) is { } invalid ? $"'{invalid}' není e-mailová adresa."
            : null;
        if (ValidationError is not null) return;

        Result = await _session.Require().SendJobDiffsAsync(_jobId, new SendJobDiffsRequest
        {
            Recipients = parsed,
            Files = _files,
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
        });
        _settings.SaveSendRecipients(Recipients.Trim());
        CloseRequested?.Invoke();
    });

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>One-line toast for a successful send, incl. what could not be attached (shared by both callers).</summary>
    public static string SuccessToast(SendJobDiffsResponse r)
    {
        string sent = $"E-mail odeslán: {r.AttachedFiles}× diff PDF{(r.Zipped ? " v ZIP" : "")}, " +
                      Format.Plural(r.Recipients, "příjemce", "příjemci", "příjemců") + ".";
        return r.SkippedFiles.Count == 0
            ? sent
            : $"{sent} Bez diff PDF (nepřiloženo): {string.Join(", ", r.SkippedFiles)}.";
    }
}
