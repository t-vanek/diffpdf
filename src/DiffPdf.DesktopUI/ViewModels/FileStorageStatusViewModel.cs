using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Read-only diagnostics of the server's file storage for the Konfigurace page: the effective root
/// and which appsettings key supplied it, reachability/writability, free space and the active limits.
/// Deliberately NOT editable — the root is an admin decision made in the server's appsettings.json;
/// this card exists so nobody has to remote onto the server just to see what is configured.
/// </summary>
public partial class FileStorageStatusViewModel : ViewModelBase
{
    /// <summary>Shown above the card so the "where do I change this" question never arises.</summary>
    public const string Hint =
        "Úložiště se nastavuje na serveru v souboru appsettings.json — klíč FileManager:RootPath "
        + "(když je prázdný, použije se ScopeSync:RootPath). Změna vyžaduje restart služby DiffPdf API; "
        + "z klienta se záměrně měnit nedá.";

    private readonly ServerSession _session;

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private IBrush _stateBrush = Palette.Muted;

    /// <summary>Extra explanation for a problematic state (probe error detail).</summary>
    [ObservableProperty] private string? _detailText;

    /// <summary>"D:\diffpdf   (z ScopeSync:RootPath)" — null while unconfigured.</summary>
    [ObservableProperty] private string? _rootText;

    [ObservableProperty] private string? _spaceText;
    [ObservableProperty] private string _limitsText = "";

    public FileStorageStatusViewModel(ServerSession session) => _session = session;

    public Task LoadAsync() => RunAsync(async () =>
    {
        var status = await _session.Require().GetFileManagerStatusAsync();
        Apply(status);
        Loaded = true;
    });

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    internal void Apply(FileManagerStatusResponse status)
    {
        (StateText, StateBrush, DetailText) = status switch
        {
            { Configured: false } => (
                "Úložiště není nakonfigurováno — správa souborů na serveru je vypnutá.",
                Palette.Bad,
                (string?)null),
            { RootExists: false } => (
                "Kořenová složka úložiště není dostupná.",
                Palette.Bad,
                status.Error),
            { Writable: false } => (
                "Kořenová složka existuje, ale nelze do ní zapisovat.",
                Palette.Bad,
                status.Error),
            _ => (
                "Úložiště je připravené a zapisovatelné.",
                Palette.Good,
                null),
        };

        RootText = status.RootPath is { } root ? $"{root}    (z {status.ResolvedFrom})" : null;
        SpaceText = status.FreeSpaceBytes is { } free && status.TotalSpaceBytes is { } total
            ? $"Volné místo: {Format.Bytes(free)} z {Format.Bytes(total)}"
            : null;
        LimitsText =
            $"Limit uploadu {status.MaxUploadSizeMB} MB · kontrola PDF obsahu "
            + (status.ValidatePdfMagicBytes ? "zapnutá" : "vypnutá")
            + $" · hledání vrací max {status.MaxSearchResults} výsledků";
    }
}
