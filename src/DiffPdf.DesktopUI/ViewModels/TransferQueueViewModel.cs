using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

public enum TransferState { Waiting, Running, Done, Skipped, Failed, Cancelled }

/// <summary>User's answer to a name conflict; the *All variants apply to the rest of the batch.</summary>
public enum OverwriteDecision { Skip, Overwrite, SkipAll, OverwriteAll }

/// <summary>One file heading from a source backend into a target backend's folder (Move deletes the source after).</summary>
public sealed record TransferRequest(
    IFileBackend Source, string SourcePath, string FileName, long? SizeBytes,
    IFileBackend Target, string TargetDirectory, bool Move);

/// <summary>One queued transfer with its live progress.</summary>
public partial class TransferItemViewModel(TransferRequest request) : ObservableObject
{
    public TransferRequest Request { get; } = request;
    public string FileName => Request.FileName;

    /// <summary>Set after the user confirms the overwrite dialog; the retry then replaces the file.</summary>
    public bool Overwrite { get; set; }

    public CancellationTokenSource Cancellation { get; } = new();

    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsFinished))]
    private TransferState _state = TransferState.Waiting;

    [ObservableProperty] private string? _error;

    public bool IsRunning => State is TransferState.Waiting or TransferState.Running;
    public bool IsFinished => !IsRunning;

    public string StateText => State switch
    {
        TransferState.Waiting => "čeká",
        TransferState.Running => $"{Progress * 100:0} %",
        TransferState.Done => "hotovo",
        TransferState.Skipped => "přeskočeno",
        TransferState.Failed => "chyba",
        TransferState.Cancelled => "zrušeno",
        _ => "",
    };

    [RelayCommand]
    private void Cancel()
    {
        if (!IsRunning) return;
        Cancellation.Cancel();
        if (State == TransferState.Waiting) State = TransferState.Cancelled; // the pump skips it
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>
/// Sequential transfer queue with per-file progress — one pump for every direction (upload, download,
/// local↔local) by streaming from the source backend into the target one. A name conflict asks the
/// injected resolver (the overwrite dialog); the *All answers settle the rest of the batch. Finished
/// clean batches clear themselves; failures stay visible until the next batch.
/// </summary>
public partial class TransferQueueViewModel : ObservableObject
{
    private bool _pumping;

    /// <summary>How long a fully successful batch stays visible before self-clearing (tests shorten it).</summary>
    internal TimeSpan CleanupDelay { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Asked when a file already exists at the target (the overwrite dialog). Set by the manager.</summary>
    public Func<TransferItemViewModel, Task<OverwriteDecision>>? OverwriteResolver { get; set; }

    /// <summary>The remembered *All answer for the current batch (null until the user picks one).</summary>
    private OverwriteDecision? _batchDecision;

    /// <summary>A batch finished; the set holds every (backend, folder) that changed — targets, and sources of moves.</summary>
    public event Action<IReadOnlyCollection<(IFileBackend Backend, string Directory)>>? BatchCompleted;

    public ObservableCollection<TransferItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private double _totalProgress;
    [ObservableProperty] private string _summaryText = "";

    /// <summary>Adds the requests to the queue and starts the pump (no-op when it already runs).</summary>
    public void Enqueue(IEnumerable<TransferRequest> requests)
    {
        // A fresh batch replaces the previous batch's finished rows (failures included — the user has seen them).
        if (!_pumping)
            Items.Clear();

        foreach (var request in requests)
        {
            var item = new TransferItemViewModel(request);
            // Live total-progress: re-aggregate whenever a file's own progress ticks.
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TransferItemViewModel.Progress)) UpdateSummary();
            };
            Items.Add(item);
        }

        HasItems = Items.Count > 0;
        UpdateSummary();
        if (!_pumping && Items.Count > 0)
            _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        _batchDecision = null; // each batch answers its conflicts afresh
        // Defer to the dispatcher before any IO: Enqueue (a click/drop handler) returns immediately
        // and the queue UI renders its Waiting rows before the first file starts streaming.
        await Task.Yield();
        var touched = new HashSet<(IFileBackend, string)>();
        try
        {
            while (Items.FirstOrDefault(i => i.State == TransferState.Waiting) is { } item)
            {
                await TransferOneAsync(item, touched);
                UpdateSummary();
            }
        }
        finally
        {
            _pumping = false;
            UpdateSummary();
            if (touched.Count > 0)
                BatchCompleted?.Invoke(touched);
            _ = ClearWhenCleanAsync();
        }
    }

    private async Task TransferOneAsync(TransferItemViewModel item, HashSet<(IFileBackend, string)> touched)
    {
        if (item.Cancellation.IsCancellationRequested)
        {
            item.State = TransferState.Cancelled;
            return;
        }

        item.State = TransferState.Running;
        var request = item.Request;
        try
        {
            try
            {
                await StreamOnceAsync(item);
            }
            catch (FileBackendConflictException) when (!item.Overwrite)
            {
                var decision = _batchDecision
                    ?? (OverwriteResolver is { } resolve ? await resolve(item) : OverwriteDecision.Skip);
                if (decision is OverwriteDecision.OverwriteAll or OverwriteDecision.SkipAll)
                    _batchDecision = decision;

                if (decision is not (OverwriteDecision.Overwrite or OverwriteDecision.OverwriteAll))
                {
                    item.State = TransferState.Skipped;
                    item.Error = "Soubor v cíli už existuje.";
                    return;
                }
                item.Overwrite = true;
                item.Progress = 0;
                await StreamOnceAsync(item);
            }

            touched.Add((request.Target, request.TargetDirectory));
            if (request.Move)
            {
                // The file landed — now remove the original (its folder changed too, so refresh it).
                await request.Source.DeleteAsync(request.SourcePath, recursive: false);
                touched.Add((request.Source, request.Source.GetParent(request.SourcePath)));
            }

            item.Progress = 1;
            item.State = TransferState.Done;
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
        }
        catch (Exception ex) when (ex is DiffPdfApiException or IOException or UnauthorizedAccessException
            or HttpRequestException or InvalidOperationException or FileBackendConflictException)
        {
            item.State = TransferState.Failed;
            item.Error = (ex as DiffPdfApiException)?.Detail ?? ex.Message;
        }
    }

    private static async Task StreamOnceAsync(TransferItemViewModel item)
    {
        var request = item.Request;
        var progress = new Progress<double>(p => item.Progress = p);
        await using var source = await request.Source.OpenReadAsync(request.SourcePath, item.Cancellation.Token);
        await request.Target.WriteFileAsync(
            request.TargetDirectory, request.FileName, source, request.SizeBytes,
            item.Overwrite, progress, item.Cancellation.Token);
    }

    /// <summary>A fully successful batch disappears on its own; anything else stays for the user to read.</summary>
    private async Task ClearWhenCleanAsync()
    {
        if (Items.Any(i => i.State is not (TransferState.Done or TransferState.Skipped)))
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
        int failed = Items.Count(i => i.State == TransferState.Failed);
        SummaryText = _pumping
            ? $"Přenos {Math.Min(finished + 1, Items.Count)}/{Items.Count}…"
            : failed > 0 ? $"Přenos dokončen, {Format.Plural(failed, "chyba", "chyby", "chyb")}." : "Přenos dokončen.";
    }
}
