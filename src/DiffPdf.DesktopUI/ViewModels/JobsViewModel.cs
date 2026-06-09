using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Úlohy: realtime list of comparison jobs (status chip + progress + outcome verdict) and a unified detail that
/// auto-loads on selection — summary chips, CI-gate verdict and ONE file-pair list (results + diff PDF when
/// finished, task progress while running) with a "jen odlišné" filter. Lifecycle actions are state-aware.
/// </summary>
public partial class JobsViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly JobProgressHubClient _hub;
    private readonly DialogService _dialogs;
    private bool _subscribed;
    private Guid? _pendingJobId;

    // Serializes list reloads (manual / realtime / filter) so they never race the shared Jobs collection.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public override string Title => "Úlohy";
    public override string Icon => "☰";
    public override int NavOrder => 3; // přímo pod Instance (úlohy patří k instancím)

    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];
    public JobStatus?[] StatusFilters { get; } = [null, .. Enum.GetValues<JobStatus>().Cast<JobStatus?>()];

    /// <summary>Filter dropdowns; the first entry ("— vše —") means "no filter".</summary>
    public const string AllFilter = "— vše —";
    public ObservableCollection<string> BranchOptions { get; } = [AllFilter];
    public ObservableCollection<string> InstanceOptions { get; } = [AllFilter];

    /// <summary>Unified file-pair list of the selected job; <see cref="FilesView"/> applies the "jen odlišné" filter.</summary>
    public ObservableCollection<FilePairLine> Files { get; } = [];
    public DataGridCollectionView FilesView { get; }

    /// <summary>Selected job's report counts, as coloured chips.</summary>
    public ObservableCollection<StatLine> Summary { get; } = [];

    [ObservableProperty] private string _filterBranch = AllFilter;
    [ObservableProperty] private string _filterInstance = AllFilter;
    [ObservableProperty] private JobStatus? _filterStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSummary), nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(CanRetry))]
    private JobRowViewModel? _selectedJob;

    public JobSummary? SelectedSummary => SelectedJob?.Job;

    [ObservableProperty] private double _liveProgress;
    [ObservableProperty] private string? _liveStatus;
    [ObservableProperty] private JobResult? _result;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private bool _showOnlyDiffering;
    [ObservableProperty] private string _fileSearch = "";
    [ObservableProperty] private string _fileCountLabel = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFilesMessage))]
    private bool _hasNoMatchingFiles;
    [ObservableProperty] private bool _hasNoJobs;

    /// <summary>True while the selected job's file list is being fetched — drives the right-panel loading spinner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFilesMessage))]
    private bool _isLoadingFiles;

    /// <summary>The "no files match" empty-state shows only when not loading, so it never overlaps the spinner.</summary>
    public bool ShowNoFilesMessage => HasNoMatchingFiles && !IsLoadingFiles;

    // State-aware lifecycle actions: only what the current status allows is enabled.
    public bool CanPause => SelectedSummary?.Status == JobStatus.Running;
    public bool CanResume => SelectedSummary?.Status == JobStatus.Paused;
    public bool CanCancel => SelectedSummary?.Status is JobStatus.Draft or JobStatus.Queued or JobStatus.Running or JobStatus.Paused;
    public bool CanRetry => SelectedSummary?.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    public JobsViewModel(ServerSession session, JobProgressHubClient hub, DialogService dialogs)
    {
        _session = session;
        _hub = hub;
        _dialogs = dialogs;
        FilesView = new DataGridCollectionView(Files)
        {
            Filter = o => o is FilePairLine f
                && (!ShowOnlyDiffering || f.IsDiffering)
                && (FileSearch.Length == 0 || f.Name.Contains(FileSearch, StringComparison.OrdinalIgnoreCase)),
        };
    }

    partial void OnShowOnlyDifferingChanged(bool value) => RefreshFilesView();
    partial void OnFileSearchChanged(string value) => RefreshFilesView();

    // Re-applies the "jen odlišné" + name-search filter and updates the "X z Y" count and empty-state hint.
    private void RefreshFilesView()
    {
        FilesView.Refresh();
        FileCountLabel = Files.Count == 0 ? "" : $"Zobrazeno {FilesView.Count} z {Files.Count}";
        HasNoMatchingFiles = Files.Count > 0 && FilesView.Count == 0;
    }

    /// <summary>Called via navigation (e.g. from a trigger) to open a specific job after the list loads.</summary>
    public void OpenJob(Guid id) => _pendingJobId = id;

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        try { await _hub.EnsureStartedAsync(); } catch { /* live progress is best-effort */ }
        if (!_subscribed)
        {
            _hub.ProgressReceived += OnProgress;
            _hub.Reconnected += OnReconnected;
            _subscribed = true;
        }
        // Join the scope group so any job finishing anywhere refreshes the list live.
        try { await _hub.JoinScopeAsync(); } catch { /* realtime is best-effort */ }

        await LoadBranchOptionsAsync();
        await LoadJobsAsync();

        if (_pendingJobId is { } id)
        {
            _pendingJobId = null;
            var row = Jobs.FirstOrDefault(j => j.Id == id);
            if (row is null && await _session.Require().GetJobAsync(id) is { } s)
            {
                row = new JobRowViewModel(s);
                Jobs.Insert(0, row);
            }
            SelectedJob = row;
        }
    });

    private async Task LoadJobsAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var list = await _session.Require().ListJobsAsync(
                FilterBranch == AllFilter ? null : FilterBranch,
                FilterInstance == AllFilter ? null : FilterInstance,
                FilterStatus);
            Reconcile(list);
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>Merges fetched jobs into <see cref="Jobs"/> by id (updates in place, inserts new, drops gone) so the
    /// selection survives a realtime refresh. Order is the server's (newest first); existing rows are not reshuffled.</summary>
    private void Reconcile(IReadOnlyList<JobSummary> list)
    {
        ListReconciler.Reconcile(Jobs, list,
            keyOf: j => j.Id.ToString(),
            rowKeyOf: r => r.Id.ToString(),
            create: j => new JobRowViewModel(j),
            update: (r, j) => r.Apply(j));
        HasNoJobs = Jobs.Count == 0;
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadJobsAsync();
        // Also pull the open job's latest file states (newly-completed pairs → openable diffs) without re-selecting.
        if (SelectedJob is { } sel) await LoadDetailAsync(sel.Id);
    });

    partial void OnFilterStatusChanged(JobStatus? value) => _ = RunAsync(LoadJobsAsync);

    // A ComboBox transiently sets its bound string to null while its ItemsSource is repopulated (Clear()).
    // Ignore that null — a real selection (incl. "— vše —") is never null/empty — so we never call the API
    // with a null branch key (which would throw in Uri.EscapeDataString).
    partial void OnFilterInstanceChanged(string value)
    {
        if (!string.IsNullOrEmpty(value)) _ = RunAsync(LoadJobsAsync);
    }

    // Picking a branch repopulates the instance dropdown (cascading) and refreshes the list.
    partial void OnFilterBranchChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return; // transient null from the ComboBox repopulating its items
        _ = RunAsync(async () =>
        {
            InstanceOptions.Clear();
            InstanceOptions.Add(AllFilter);
            if (value != AllFilter)
                foreach (var i in await _session.Require().ListInstancesAsync(value)) InstanceOptions.Add(i.Key);
            if (!InstanceOptions.Contains(FilterInstance)) FilterInstance = AllFilter; // may re-trigger a (gated) reload
            await LoadJobsAsync();
        });
    }

    private async Task LoadBranchOptionsAsync()
    {
        var branches = await _session.Require().ListBranchesAsync();
        BranchOptions.Clear();
        BranchOptions.Add(AllFilter);
        foreach (var b in branches) BranchOptions.Add(b.Key);
        if (!BranchOptions.Contains(FilterBranch)) FilterBranch = AllFilter;
    }

    public override Task DeactivateAsync()
    {
        if (_subscribed)
        {
            _hub.ProgressReceived -= OnProgress;
            _hub.Reconnected -= OnReconnected;
            _subscribed = false;
        }
        return Task.CompletedTask;
    }

    private void OnReconnected() => _ = ReloadQuietlyAsync();

    private async Task ReloadQuietlyAsync()
    {
        try { await LoadJobsAsync(); }
        catch { /* best-effort realtime refresh */ }
    }

    private void OnProgress(JobProgress p)
    {
        bool terminal = p.Status is "Completed" or "Failed" or "Cancelled";
        bool selected = SelectedJob?.Id == p.JobId;
        var row = Jobs.FirstOrDefault(r => r.Id == p.JobId);
        if (row is null)
        {
            _ = ReloadQuietlyAsync(); // a job we don't have yet (started elsewhere) → fetch it into the list
        }
        else
        {
            // A job whose RecoveredAt just appeared was auto-recovered after an interruption — light its
            // "Obnoveno" chip and toast once, for ANY job in the list (not only the selected/terminal one the
            // per-tick update below is limited to). Runs first so the chip survives the status update that follows.
            if (p.RecoveredAt is not null && !row.WasRecovered)
            {
                row.Apply(row.Job with { RecoveredAt = p.RecoveredAt });
                _dialogs.ShowToast($"Obnoveno porovnání {row.Job.BranchKey}/{row.Job.InstanceKey} po přerušení.", ToastKind.Info);
            }

            if (terminal || selected)
            {
                // Update the open job live, or any job that just finished. Skip per-tick churn for the rest: jobProgress
                // is broadcast to every client, so re-applying every running tick of every job would be needless work.
                row.Apply(row.Job with
                {
                    Status = Enum.TryParse<JobStatus>(p.Status, out var st) ? st : row.Job.Status,
                    Progress = p.Progress, ProcessedCount = p.ProcessedCount, TotalCount = p.TotalCount,
                });
                if (terminal) _ = ReloadQuietlyAsync(); // refresh the verdict (counts come from the now-written report)
            }
        }

        if (!selected) return;
        LiveProgress = p.Progress;
        LiveStatus = p.Status;
        Info = $"{p.Status}: {p.ProcessedCount}/{p.TotalCount}";
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanRetry));
        if (terminal) _ = LoadDetailAsync(p.JobId); // pull the report/files/verdict into the open detail
    }

    partial void OnSelectedJobChanged(JobRowViewModel? value)
    {
        Files.Clear();
        Summary.Clear();
        Result = null;
        RefreshFilesView();
        if (value is null) { LiveStatus = null; LiveProgress = 0; IsLoadingFiles = false; return; }

        LiveProgress = value.Job.Progress;
        LiveStatus = value.StatusText;
        _ = RunAsync(async () =>
        {
            await _hub.JoinJobAsync(value.Id);
            await LoadDetailAsync(value.Id);
        });
    }

    /// <summary>Auto-loads everything for the selected job: the finished report (summary + files + diff PDFs) or,
    /// while it is still running, the live task list — plus the CI-gate result.</summary>
    private async Task LoadDetailAsync(Guid id)
    {
        var client = _session.Require();
        bool finished = SelectedJob?.Job.Status == JobStatus.Completed;

        Files.Clear();
        Summary.Clear();
        IsLoadingFiles = true;
        try
        {
            // A completed job has the rich report (per-file results + diff PDFs). Only then do we ask for it —
            // requesting a report/result for a running job would 404, so we avoid using exceptions as control flow.
            if (finished)
            {
                try
                {
                    var report = await client.GetReportAsync(id);
                    if (SelectedJob?.Id != id) return; // selection moved on while we awaited
                    foreach (var f in report.Files) Files.Add(FilePairLine.FromResult(f));
                    BuildSummary(report);
                }
                catch { /* report pruned by retention → fall back to task-level state below */ }
            }

            if (Files.Count == 0) // running / failed / cancelled / pruned → show task progress
            {
                try
                {
                    foreach (var t in await client.GetTasksAsync(id))
                        if (SelectedJob?.Id == id) Files.Add(FilePairLine.FromTask(t));
                }
                catch { /* best-effort */ }
            }
            RefreshFilesView();

            if (finished)
                try { var r = await client.GetResultAsync(id); if (SelectedJob?.Id == id) Result = r; }
                catch { /* CI result is best-effort */ }
        }
        finally
        {
            // Guard against a stale load (selection moved on mid-fetch) clearing the spinner the new load just set.
            if (SelectedJob?.Id == id) IsLoadingFiles = false;
        }
    }

    private void BuildSummary(BatchComparisonReport r)
    {
        Summary.Clear();
        Summary.Add(new("Celkem", r.Total));
        Summary.Add(new("Shodné", r.Identical) { Tone = StatTone.Good });
        Summary.Add(new("Odlišné", r.Differing) { Tone = StatTone.Failed });
        Summary.Add(new("Jen ve staré", r.OnlyInOld) { Tone = StatTone.Warning });
        Summary.Add(new("Jen v nové", r.OnlyInNew) { Tone = StatTone.Warning });
        Summary.Add(new("Chyby", r.Errors) { Tone = StatTone.Failed });
        Summary.Add(new("Chyby v obsahu", r.FilesWithContentErrors) { Tone = StatTone.Warning });
    }

    [RelayCommand] private Task PauseAsync() => ActAsync(c => c.PauseJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task ResumeAsync() => ActAsync(c => c.ResumeJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task CancelAsync() => ActAsync(c => c.CancelJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task RetryAsync() => ActAsync(c => c.RetryJobAsync(SelectedJob!.Id));

    [RelayCommand]
    private Task CopyJobIdAsync() =>
        SelectedSummary is { } s ? _dialogs.CopyToClipboardAsync(s.Id.ToString(), "ID úlohy zkopírováno.") : Task.CompletedTask;

    private Task ActAsync(Func<DiffPdfClient, Task<JobSummary>> action) => RunAsync(async () =>
    {
        if (SelectedJob is null) throw new InvalidOperationException("Vyber úlohu.");
        var updated = await action(_session.Require());
        SelectedJob.Apply(updated);
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanRetry));
        Info = $"Úloha: {updated.Status}.";
    });

    /// <summary>Double-clicking a file row opens its full detail (and diff preview / save actions) in a separate
    /// window — the Úlohy panel is too narrow to show more than status + name inline.</summary>
    [RelayCommand]
    private void OpenFileDetail(FilePairLine? line)
    {
        if (SelectedJob is null || line is null) return;
        _dialogs.ShowFileDetail(new FilePairDetailViewModel(SelectedJob.Id, line, _session, _dialogs));
    }
}
