using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Ad-hoc single-pair comparison. Shows a human verdict (identical / differs + page &amp; similarity
/// summary) and renders the highlighted diff PDF inline in an embedded WebView2 — no raw JSON. The diff PDF is
/// streamed from the server, written to a temp file, and the webview points at its <c>file://</c> URI
/// (same approach as the file-pair detail window).</summary>
public partial class SingleCompareViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private string? _tempPath;

    public override string Title => "Jednorázové porovnání";
    public override string Icon => "⇄";
    public override int NavOrder => 6;

    public ComparisonOptionsViewModel Options { get; } = new();

    [ObservableProperty] private string _oldPath = string.Empty;
    [ObservableProperty] private string _newPath = string.Empty;

    // Verdict surface.
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _verdictText = string.Empty;
    [ObservableProperty] private IBrush _verdictBrush = Palette.Muted;
    [ObservableProperty] private string _summaryText = string.Empty;

    /// <summary>True when a highlighted diff PDF is available to preview (false when the documents are identical).</summary>
    [ObservableProperty] private bool _hasDiff;

    /// <summary>The <c>file://</c> URI of the downloaded diff PDF the embedded webview renders; null until loaded.</summary>
    [ObservableProperty] private string? _fileUri;

    public SingleCompareViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    [RelayCommand]
    private async Task BrowseOldAsync()
    {
        if (await _dialogs.OpenPdfAsync("Vyber starou verzi PDF") is { } path) OldPath = path;
    }

    [RelayCommand]
    private async Task BrowseNewAsync()
    {
        if (await _dialogs.OpenPdfAsync("Vyber novou verzi PDF") is { } path) NewPath = path;
    }

    [RelayCommand]
    private Task CompareAsync() => RunAsync(async () =>
    {
        HasResult = false;
        HasDiff = false;
        FileUri = null;
        CleanupTemp();

        // The comparison runs on the server, which in production is a different machine — so upload the bytes of
        // the (locally picked / dropped) PDFs rather than a path the server could not resolve.
        if (!File.Exists(OldPath)) throw new InvalidOperationException($"Stará verze nenalezena: {OldPath}");
        if (!File.Exists(NewPath)) throw new InvalidOperationException($"Nová verze nenalezena: {NewPath}");

        var oldBytes = await File.ReadAllBytesAsync(OldPath);
        var newBytes = await File.ReadAllBytesAsync(NewPath);
        var preview = await _session.Require().CompareSinglePreviewUploadAsync(
            oldBytes, Path.GetFileName(OldPath), newBytes, Path.GetFileName(NewPath), Options.ToOptions());

        VerdictText = preview.Identical ? "Dokumenty jsou shodné" : "Dokumenty se liší";
        VerdictBrush = preview.Identical ? Palette.Good : Palette.Bad;

        var parts = new List<string>
        {
            preview.OldPageCount == preview.NewPageCount
                ? $"{preview.NewPageCount} stran"
                : $"{preview.OldPageCount} → {preview.NewPageCount} stran",
        };
        if (!preview.Identical) parts.Add($"{preview.DifferingPages} odlišných");
        if (!double.IsNaN(preview.Similarity)) parts.Add($"podobnost {preview.Similarity * 100:0} %");
        if (preview.ContentErrorCount > 0) parts.Add($"{preview.ContentErrorCount} obsahových chyb");
        SummaryText = string.Join("   ·   ", parts);

        if (preview.DiffPdf is { } bytes)
        {
            // The webview renders a local file: write the diff to a temp PDF (cleaned up on the next compare).
            var dir = Path.Combine(Path.GetTempPath(), "DiffPdf", "single");
            Directory.CreateDirectory(dir);
            _tempPath = Path.Combine(dir, $"{Guid.NewGuid():N}.diff.pdf");
            await File.WriteAllBytesAsync(_tempPath, bytes);
            FileUri = new Uri(_tempPath).AbsoluteUri;
            HasDiff = true;
        }

        HasResult = true;
    });

    /// <summary>Opens the downloaded diff PDF in the system viewer — the fallback action when the embedded
    /// WebView2 preview is unavailable (missing runtime), and a handy escape hatch otherwise.</summary>
    [RelayCommand]
    private void OpenExternally()
    {
        if (_tempPath is { } p && File.Exists(p))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    }

    /// <summary>Deletes the previous preview's temp PDF (best-effort) — the file the webview may still hold open
    /// gets a fresh unique name each compare, so a locked delete here is harmless.</summary>
    private void CleanupTemp()
    {
        try { if (_tempPath is { } p && File.Exists(p)) File.Delete(p); }
        catch { /* best-effort cleanup */ }
        _tempPath = null;
    }
}
