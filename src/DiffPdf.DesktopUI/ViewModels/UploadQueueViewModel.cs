using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

public enum UploadState { Waiting, Uploading, Done, Skipped, Failed, Cancelled }

/// <summary>User's answer to a name conflict; the *All variants apply to the rest of the batch.</summary>
public enum OverwriteDecision { Skip, Overwrite, SkipAll, OverwriteAll }

/// <summary>One queued upload: a local PDF heading for a target folder on the server.</summary>
public partial class UploadItemViewModel : ObservableObject
{
    public UploadItemViewModel(string localPath, string targetDirectory)
    {
        LocalPath = localPath;
        TargetDirectory = targetDirectory;
        FileName = Path.GetFileName(localPath);
    }

    public string LocalPath { get; }
    public string TargetDirectory { get; }
    public string FileName { get; }

    /// <summary>Set after the user confirms the overwrite dialog; the retry then replaces the file.</summary>
    public bool Overwrite { get; set; }

    public CancellationTokenSource Cancellation { get; } = new();

    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsFinished))]
    private UploadState _state = UploadState.Waiting;

    [ObservableProperty] private string? _error;

    public bool IsRunning => State is UploadState.Waiting or UploadState.Uploading;
    public bool IsFinished => !IsRunning;

    public string StateText => State switch
    {
        UploadState.Waiting => "čeká",
        UploadState.Uploading => $"{Progress * 100:0} %",
        UploadState.Done => "hotovo",
        UploadState.Skipped => "přeskočeno",
        UploadState.Failed => "chyba",
        UploadState.Cancelled => "zrušeno",
        _ => "",
    };

    [RelayCommand]
    private void Cancel()
    {
        if (!IsRunning) return;
        Cancellation.Cancel();
        if (State == UploadState.Waiting) State = UploadState.Cancelled; // the pump skips it
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>
/// Sequential upload queue with per-file progress. One file per request (accurate progress, simple
/// cancel/retry); a name conflict asks the injected resolver (the overwrite dialog) and retries with
/// overwrite. Finished batches clear themselves shortly after the last item completes successfully;
/// failures stay visible until the next batch.
/// </summary>
public partial class UploadQueueViewModel : ObservableObject
{
    private readonly ServerSession _session;
    private bool _pumping;

    /// <summary>How long a fully successful batch stays visible before self-clearing (tests shorten it).</summary>
    internal TimeSpan CleanupDelay { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Asked when a file already exists on the server (the overwrite dialog). The *All answers
    /// are remembered for the rest of the batch, so one click can settle every remaining conflict.</summary>
    public Func<UploadItemViewModel, Task<OverwriteDecision>>? OverwriteResolver { get; set; }

    /// <summary>The remembered *All answer for the current batch (null until the user picks one).</summary>
    private OverwriteDecision? _batchDecision;

    /// <summary>A batch finished; the set holds each target folder that received at least one file.</summary>
    public event Action<IReadOnlyCollection<string>>? BatchCompleted;

    public ObservableCollection<UploadItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private double _totalProgress;
    [ObservableProperty] private string _summaryText = "";

    public UploadQueueViewModel(ServerSession session) => _session = session;

    /// <summary>Adds the files to the queue and starts the pump (no-op when it already runs).</summary>
    public void Enqueue(string targetDirectory, IEnumerable<string> localPaths)
    {
        // A fresh batch replaces the previous batch's finished rows (failures included — the user has seen them).
        if (!_pumping)
            Items.Clear();

        foreach (string localPath in localPaths)
        {
            var item = new UploadItemViewModel(localPath, targetDirectory);
            // Live total-progress: re-aggregate whenever a file's own progress ticks.
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UploadItemViewModel.Progress)) UpdateSummary();
            };
            Items.Add(item);
        }

        HasItems = Items.Count > 0;
        UpdateSummary();
        if (!_pumping)
            _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        _batchDecision = null; // each batch answers its conflicts afresh
        // Defer to the dispatcher before any IO: Enqueue (a click/drop handler) returns immediately
        // and the queue UI renders its Waiting rows before the first file starts streaming.
        await Task.Yield();
        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (Items.FirstOrDefault(i => i.State == UploadState.Waiting) is { } item)
            {
                await UploadOneAsync(item, touchedDirectories);
                UpdateSummary();
            }
        }
        finally
        {
            _pumping = false;
            UpdateSummary();
            if (touchedDirectories.Count > 0)
                BatchCompleted?.Invoke(touchedDirectories);
            _ = ClearWhenCleanAsync();
        }
    }

    private async Task UploadOneAsync(UploadItemViewModel item, HashSet<string> touchedDirectories)
    {
        if (item.Cancellation.IsCancellationRequested)
        {
            item.State = UploadState.Cancelled;
            return;
        }

        item.State = UploadState.Uploading;
        try
        {
            var response = await SendAsync(item);

            if (!item.Overwrite && response.ErrorCode == FileUploadErrorCodes.Exists)
            {
                var decision = _batchDecision
                    ?? (OverwriteResolver is { } resolve ? await resolve(item) : OverwriteDecision.Skip);
                if (decision is OverwriteDecision.OverwriteAll or OverwriteDecision.SkipAll)
                    _batchDecision = decision;

                if (decision is OverwriteDecision.Overwrite or OverwriteDecision.OverwriteAll)
                {
                    item.Overwrite = true;
                    item.Progress = 0;
                    response = await SendAsync(item);
                }
            }

            if (response.Uploaded)
            {
                item.Progress = 1;
                item.State = UploadState.Done;
                touchedDirectories.Add(item.TargetDirectory);
            }
            else if (response.ErrorCode == FileUploadErrorCodes.Exists)
            {
                item.State = UploadState.Skipped;
                item.Error = "Soubor na serveru už existuje.";
            }
            else
            {
                item.State = UploadState.Failed;
                item.Error = response.ErrorMessage ?? response.ErrorCode ?? "Nahrání selhalo.";
            }
        }
        catch (OperationCanceledException)
        {
            item.State = UploadState.Cancelled;
        }
        catch (DiffPdfApiException ex)
        {
            item.State = UploadState.Failed;
            item.Error = ex.Detail ?? ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            item.State = UploadState.Failed;
            item.Error = ex.Message;
        }
    }

    private async Task<UploadFileResponse> SendAsync(UploadItemViewModel item)
    {
        var progress = new Progress<double>(p => item.Progress = p);
        await using var stream = File.OpenRead(item.LocalPath);
        return await _session.Require().UploadFileAsync(
            item.TargetDirectory, item.FileName, stream, item.Overwrite, progress, item.Cancellation.Token);
    }

    /// <summary>A fully successful batch disappears on its own; anything else stays for the user to read.</summary>
    private async Task ClearWhenCleanAsync()
    {
        if (Items.Any(i => i.State is not (UploadState.Done or UploadState.Skipped)))
            return;
        await Task.Delay(CleanupDelay);
        if (_pumping) return; // a new batch started meanwhile
        Items.Clear();
        HasItems = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        HasItems = Items.Count > 0;
        if (Items.Count == 0)
        {
            SummaryText = "";
            TotalProgress = 0;
            return;
        }
        int finished = Items.Count(i => i.IsFinished);
        TotalProgress = Items.Average(i => i.IsFinished ? 1d : i.Progress);
        int failed = Items.Count(i => i.State == UploadState.Failed);
        SummaryText = _pumping
            ? $"Nahrávání {Math.Min(finished + 1, Items.Count)}/{Items.Count}…"
            : failed > 0 ? $"Nahrávání dokončeno, {Format.Plural(failed, "chyba", "chyby", "chyb")}." : "Nahrávání dokončeno.";
    }
}
