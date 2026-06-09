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

    private const int JobsPageSize = 100;
    private const int FilesPageSize = 150;
    private int _jobsTotal;   // total jobs matching the filter (from X-Total-Count) — drives "load more on scroll"
    private int _filesTotal;  // total file-pair tasks matching the search/"jen odlišné" filter for the selected job
    private CancellationTokenSource? _searchDebounce;
    private bool _loadingMoreFiles; // guards against the scroll handler firing a duplicate file page-load

    // Scopes ("branchKey/instanceKey") that have a configured print source — gates "Vytisknout a porovnat".
    private readonly HashSet<string> _printScopes = new(StringComparer.OrdinalIgnoreCase);

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrintAndCompare))]
    [NotifyCanExecuteChangedFor(nameof(PrintAndCompareCommand))]
    private string _filterBranch = AllFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrintAndCompare))]
    [NotifyCanExecuteChangedFor(nameof(PrintAndCompareCommand))]
    private string _filterInstance = AllFilter;

    [ObservableProperty] private JobStatus? _filterStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSummary), nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(CanRetry))]
    private JobRowViewModel? _selectedJob;

    public JobSummary? SelectedSummary => SelectedJob?.Job;

    [ObservableProperty] private double _liveProgress;
    [ObservableProperty] private string? _liveStatus;
    [ObservableProperty] private JobResult? _result;
    [ObservableProperty] private string? _info;

    /// <summary>Live status line of an in-flight "Vytisknout a porovnat" (tisk → stahování → porovnání); null when idle.</summary>
    [ObservableProperty] private string? _printStatus;
    [ObservableProperty] private bool _showOnlyDiffering = true; // default: show only differing pairs (the usual interest)
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

    /// <summary>More rows exist beyond what's loaded — drives the "Načíst další" button (+ scroll auto-load).</summary>
    public bool MoreJobsAvailable => Jobs.Count < _jobsTotal;
    public bool MoreFilesAvailable => Files.Count < _filesTotal;

    // State-aware lifecycle actions: only what the current status allows is enabled.
    public bool CanPause => SelectedSummary?.Status == JobStatus.Running;
    public bool CanResume => SelectedSummary?.Status == JobStatus.Paused;
    public bool CanCancel => SelectedSummary?.Status is JobStatus.Draft or JobStatus.Queued or JobStatus.Running or JobStatus.Paused;
    public bool CanRetry => SelectedSummary?.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>"Vytisknout a porovnat" is available when a specific instance is selected and it has a print source.</summary>
    public bool CanPrintAndCompare =>
        FilterBranch != AllFilter && FilterInstance != AllFilter &&
        _printScopes.Contains($"{FilterBranch}/{FilterInstance}");

    public JobsViewModel(ServerSession session, JobProgressHubClient hub, DialogService dialogs)
    {
        _session = session;
        _hub = hub;
        _dialogs = dialogs;
        // The file list is paged + filtered server-side (search + "jen odlišné"), so the view shows the loaded rows
        // as-is — no client-side predicate (which would only ever see the already-loaded page).
        FilesView = new DataGridCollectionView(Files);
    }

    // The search + "jen odlišné" filter run server-side over the whole job, so a change reloads page 1 (search
    // debounced so we don't hit the server on every keystroke; the checkbox reloads immediately).
    partial void OnShowOnlyDifferingChanged(bool value) => ReloadFilesFromFilter();
    partial void OnFileSearchChanged(string value) => DebounceFileSearch();

    private void ReloadFilesFromFilter()
    {
        if (SelectedJob is not { } sel) return;
        _ = RunAsync(async () =>
        {
            IsLoadingFiles = true;
            try { await LoadFilesPageAsync(sel.Id, 0, replace: true); RefreshFilesView(); }
            finally { if (SelectedJob?.Id == sel.Id) IsLoadingFiles = false; }
        });
    }

    private void DebounceFileSearch()
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(300, token); } catch { return; }
            Avalonia.Threading.Dispatcher.UIThread.Post(ReloadFilesFromFilter);
        });
    }

    // Updates the "loaded / total" count + empty-state hint (server already paged + filtered — no client filter).
    private void RefreshFilesView()
    {
        FileCountLabel = _filesTotal == 0 ? "" : $"Zobrazeno {Files.Count} z {_filesTotal}";
        HasNoMatchingFiles = SelectedJob is not null && !IsLoadingFiles && _filesTotal == 0 && Files.Count == 0;
        OnPropertyChanged(nameof(MoreFilesAvailable));
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
        await LoadPrintSourcesAsync();
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

    private Task LoadJobsAsync() => ReplaceJobsAsync(JobsPageSize); // page 1 — reset (initial load / filter change)

    // Fetches [0, limit) jobs from offset 0 and reconciles in place (keeps selection; drops jobs no longer present).
    private async Task ReplaceJobsAsync(int limit)
    {
        await _loadGate.WaitAsync();
        try
        {
            var (items, total) = await _session.Require().ListJobsPagedAsync(
                FilterBranch == AllFilter ? null : FilterBranch,
                FilterInstance == AllFilter ? null : FilterInstance,
                FilterStatus, limit, 0);
            _jobsTotal = total;
            Reconcile(items);
            OnPropertyChanged(nameof(MoreJobsAvailable));
        }
        finally { _loadGate.Release(); }
    }

    [RelayCommand]
    private Task LoadMoreJobsAsync() => RunAsync(async () =>
    {
        if (Jobs.Count >= _jobsTotal) return;
        await _loadGate.WaitAsync();
        try
        {
            var (items, total) = await _session.Require().ListJobsPagedAsync(
                FilterBranch == AllFilter ? null : FilterBranch,
                FilterInstance == AllFilter ? null : FilterInstance,
                FilterStatus, JobsPageSize, Jobs.Count);
            _jobsTotal = total;
            var have = Jobs.Select(r => r.Id).ToHashSet();
            foreach (var j in items) // append the next page (server's newest-first order preserved)
                if (have.Add(j.Id)) Jobs.Add(new JobRowViewModel(j));
            HasNoJobs = Jobs.Count == 0;
            OnPropertyChanged(nameof(MoreJobsAvailable));
        }
        finally { _loadGate.Release(); }
    });

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
        // Refetch everything currently loaded (not just page 1) so a realtime refresh never truncates pages the
        // user already scrolled in; Reconcile updates in place + inserts new jobs at the top.
        try { await ReplaceJobsAsync(Math.Max(JobsPageSize, Jobs.Count)); }
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
        _filesTotal = 0;
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

    /// <summary>Auto-loads the selected job's detail: the summary chips (from the lightweight /result counts for a
    /// finished job) plus the first page of its file list — paged tasks, which serve a running OR finished job
    /// (the report is built 1:1 from tasks), filtered server-side by the current search + "jen odlišné".</summary>
    private async Task LoadDetailAsync(Guid id)
    {
        var client = _session.Require();
        bool finished = SelectedJob?.Job.Status == JobStatus.Completed;

        Files.Clear();
        Summary.Clear();
        _filesTotal = 0;
        IsLoadingFiles = true;
        try
        {
            // Summary chips (finished only) from the lightweight /result counts — no need to download every file
            // row just to show the totals; the file list itself is paged below.
            if (finished)
            {
                try
                {
                    var r = await client.GetResultAsync(id);
                    if (SelectedJob?.Id != id) return; // selection moved on while we awaited
                    Result = r;
                    BuildSummary(r.Summary);
                }
                catch { /* result/summary best-effort */ }
            }

            await LoadFilesPageAsync(id, offset: 0, replace: true);

            // Old finished job whose tasks were pruned by retention (no filter active) → fall back to the full report.
            if (finished && _filesTotal == 0 && Files.Count == 0
                && string.IsNullOrEmpty(FileSearch) && !ShowOnlyDiffering
                && (SelectedJob?.Job.TotalCount ?? 0) > 0)
            {
                try
                {
                    var report = await client.GetReportAsync(id);
                    if (SelectedJob?.Id != id) return;
                    foreach (var f in report.Files) Files.Add(FilePairLine.FromResult(f));
                    _filesTotal = Files.Count;
                    if (Summary.Count == 0) BuildSummary(report);
                }
                catch { /* report also pruned → nothing to show */ }
            }

            RefreshFilesView();
        }
        finally
        {
            // Guard against a stale load (selection moved on mid-fetch) clearing the spinner the new load just set.
            if (SelectedJob?.Id == id) IsLoadingFiles = false;
        }
    }

    // Loads one page of the selected job's tasks (filtered server-side) and appends or replaces into Files.
    private async Task LoadFilesPageAsync(Guid id, int offset, bool replace)
    {
        try
        {
            var (items, total) = await _session.Require().GetTasksAsync(
                id, FilesPageSize, offset,
                string.IsNullOrWhiteSpace(FileSearch) ? null : FileSearch, ShowOnlyDiffering);
            if (SelectedJob?.Id != id) return; // selection moved on
            if (replace) Files.Clear();
            foreach (var t in items) Files.Add(FilePairLine.FromTask(t));
            _filesTotal = total;
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private Task LoadMoreFilesAsync() => RunAsync(async () =>
    {
        if (SelectedJob is not { } sel || _loadingMoreFiles || Files.Count >= _filesTotal) return;
        _loadingMoreFiles = true;
        try { await LoadFilesPageAsync(sel.Id, Files.Count, replace: false); RefreshFilesView(); }
        finally { _loadingMoreFiles = false; }
    });

    // Report-based summary (the pruned-job fallback) reuses the /result-counts builder below.
    private void BuildSummary(BatchComparisonReport r) => BuildSummary(new JobResultSummary
    {
        Total = r.Total, Identical = r.Identical, Differing = r.Differing,
        OnlyInOld = r.OnlyInOld, OnlyInNew = r.OnlyInNew, Errors = r.Errors,
        FilesWithContentErrors = r.FilesWithContentErrors,
    });

    private void BuildSummary(JobResultSummary s)
    {
        Summary.Clear();
        Summary.Add(new("Celkem", s.Total));
        Summary.Add(new("Shodné", s.Identical) { Tone = StatTone.Good });
        Summary.Add(new("Odlišné", s.Differing) { Tone = StatTone.Failed });
        Summary.Add(new("Jen ve staré", s.OnlyInOld) { Tone = StatTone.Warning });
        Summary.Add(new("Jen v nové", s.OnlyInNew) { Tone = StatTone.Warning });
        Summary.Add(new("Chyby", s.Errors) { Tone = StatTone.Failed });
        Summary.Add(new("Chyby v obsahu", s.FilesWithContentErrors) { Tone = StatTone.Warning });
    }

    [RelayCommand] private Task PauseAsync() => ActAsync(c => c.PauseJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task ResumeAsync() => ActAsync(c => c.ResumeJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task CancelAsync() => ActAsync(c => c.CancelJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task RetryAsync() => ActAsync(c => c.RetryJobAsync(SelectedJob!.Id));

    [RelayCommand]
    private Task CopyJobIdAsync() =>
        SelectedSummary is { } s ? _dialogs.CopyToClipboardAsync(s.Id.ToString(), "ID úlohy zkopírováno.") : Task.CompletedTask;

    // ── Vytisknout a porovnat (D3Soft Tisk SOA) ──────────────────────────────

    // Loads the set of scopes with a configured print source (optional feature; ignored when unavailable).
    private async Task LoadPrintSourcesAsync()
    {
        try
        {
            var sources = await _session.Require().GetPrintSourcesAsync();
            _printScopes.Clear();
            foreach (var s in sources.Scopes) _printScopes.Add(s);
        }
        catch { /* the print feature is optional — leave the action hidden when it isn't configured/available */ }
        OnPropertyChanged(nameof(CanPrintAndCompare));
        PrintAndCompareCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Prints a fresh sample batch for the selected instance and compares it to the previous batch. The POST
    /// only enqueues a durable orchestration; we then follow the operation off the busy path (so the UI isn't
    /// blocked for minutes) and select the resulting comparison job when it launches.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrintAndCompare))]
    private Task PrintAndCompareAsync() => RunAsync(async () =>
    {
        var branch = FilterBranch;
        var instance = FilterInstance;
        var op = await _session.Require().StartPrintAndCompareAsync(branch, instance);
        _dialogs.ShowToast($"Tisk a porovnání spuštěno pro {branch}/{instance}.", ToastKind.Info);
        PrintStatus = "Tisknu vzory…";
        _ = PollPrintOperationAsync(op.OperationId, branch, instance);
    });

    // Detached follower: polls the operation status (off the busy path), surfaces phase + result, and selects
    // the launched comparison job. Resumes on the UI thread after each await (Avalonia sync context), so the
    // bound PrintStatus + toasts are touched safely.
    private async Task PollPrintOperationAsync(Guid operationId, string branch, string instance)
    {
        var client = _session.Require();
        for (var i = 0; i < 600; i++) // ~20 min ceiling at a 2s cadence
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            PrintOperationResponse? op;
            try { op = await client.GetPrintOperationAsync(operationId); }
            catch { continue; } // transient — keep following
            if (op is null) continue;

            PrintStatus = PhaseLabel(op.Phase);
            switch (op.Phase)
            {
                case "Completed":
                    PrintStatus = null;
                    _dialogs.ShowToast($"Porovnání spuštěno pro {branch}/{instance}.", ToastKind.Success);
                    await LoadJobsAsync();
                    if (op.JobId is { } jobId && Jobs.FirstOrDefault(j => j.Id == jobId) is { } row)
                        SelectedJob = row;
                    return;
                case "NoPreviousBatch":
                    PrintStatus = null;
                    _dialogs.ShowToast(op.Error ?? "Nenašla se předchozí dávka k porovnání.", ToastKind.Info);
                    return;
                case "Failed":
                    PrintStatus = null;
                    _dialogs.ShowToast($"Tisk a porovnání selhalo: {op.Error}", ToastKind.Error);
                    return;
            }
        }
        PrintStatus = null; // stopped following after the ceiling; the comparison (if any) still shows in the list
    }

    private static string? PhaseLabel(string phase) => phase switch
    {
        "Printing" => "Tisknu vzory…",
        "FindingPrevious" => "Hledám předchozí dávku…",
        "Fetching" => "Stahuji dávky…",
        "Launching" => "Spouštím porovnání…",
        _ => null,
    };

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
