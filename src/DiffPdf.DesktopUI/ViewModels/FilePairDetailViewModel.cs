using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Backs the file-pair detail window opened by double-clicking a file in a job. The highlighted diff PDF is
/// rendered full-window in an embedded WebView2 (Edge); a slim toolbar carries the status, file name, the
/// compact metric summary and "Uložit PDF". The artifact is downloaded when the window opens and written to a
/// temp file the window cleans up on close. Built from a <see cref="FilePairLine"/>; the diff load needs the
/// owning job's id to pull the artifact (<see cref="DiffPdfClient.DownloadArtifactAsync"/>).
/// </summary>
public sealed partial class FilePairDetailViewModel : ViewModelBase
{
    private readonly Guid _jobId;
    private readonly FilePairLine _line;
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private readonly string? _artifactName;
    private byte[]? _bytes;
    private string? _tempPath;

    public FilePairDetailViewModel(Guid jobId, FilePairLine line, ServerSession session, DialogService dialogs)
    {
        _jobId = jobId;
        _line = line;
        _session = session;
        _dialogs = dialogs;
        _artifactName = line.Diff?.HighlightedPdfPath is { } p ? Path.GetFileName(p) : null;
    }

    public string FileName => _line.Name;
    public string StatusIcon => _line.Icon;
    public string StatusText => _line.StatusText;
    public IBrush StatusBrush => _line.Brush;

    /// <summary>Compact one-line metric summary (similarity · differing pages · compare time, or task state).</summary>
    public string Detail => _line.Detail;

    /// <summary>The comparison's own error (distinct from <see cref="ViewModelBase.Error"/>, which surfaces action failures).</summary>
    public string? PairError => _line.Diff?.Error;

    /// <summary>True when the run produced a highlighted diff PDF — only then is there a preview to render / save.</summary>
    public bool HasDiff => _line.HasDiff;

    /// <summary>The <c>file://</c> URI of the downloaded diff PDF the embedded webview renders; null until loaded.</summary>
    [ObservableProperty] private string? _fileUri;

    /// <summary>Feedback for the save action (success path; failures land in <see cref="ViewModelBase.Error"/>).</summary>
    [ObservableProperty] private string? _status;

    /// <summary>Downloads the diff artifact and points the embedded webview at a temp copy. Called when the window opens.</summary>
    public Task LoadAsync() => RunAsync(async () =>
    {
        if (_artifactName is null) return; // no diff → nothing to render
        _bytes = await _session.Require().DownloadArtifactAsync(_jobId, _artifactName);

        // The webview renders a local file: write the artifact to a temp PDF the window deletes on close.
        var dir = Path.Combine(Path.GetTempPath(), "DiffPdf", "preview");
        Directory.CreateDirectory(dir);
        _tempPath = Path.Combine(dir, $"{Guid.NewGuid():N}-{_artifactName}");
        await File.WriteAllBytesAsync(_tempPath, _bytes);
        FileUri = new Uri(_tempPath).AbsoluteUri;
    });

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        if (_artifactName is null) throw new InvalidOperationException("Pro tuto dvojici není diff-artefakt.");
        _bytes ??= await _session.Require().DownloadArtifactAsync(_jobId, _artifactName);
        var saved = await _dialogs.SaveBytesAsync(_artifactName, _bytes);
        Status = saved is null ? "Uložení zrušeno." : $"Uloženo: {saved}";
    });

    /// <summary>Deletes the temp PDF the webview rendered (best-effort) — called when the window closes.</summary>
    public void Cleanup()
    {
        try { if (_tempPath is { } p && File.Exists(p)) File.Delete(p); }
        catch { /* best-effort cleanup */ }
    }
}
