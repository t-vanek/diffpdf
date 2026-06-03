using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Jobs: list + filter, detail (live progress via SignalR, tasks, report, CI result, artifacts), lifecycle actions.</summary>
public partial class JobsViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly JobProgressHubClient _hub;
    private readonly DialogService _dialogs;
    private Guid? _pendingJobId;

    public override string Title => "Jobs";
    public override int NavOrder => 9;

    public ObservableCollection<JobSummary> Jobs { get; } = [];
    public ObservableCollection<FilePairTaskSummary> Tasks { get; } = [];
    public JobStatus?[] StatusFilters { get; } = [null, .. Enum.GetValues<JobStatus>().Cast<JobStatus?>()];

    [ObservableProperty] private string _filterBranch = string.Empty;
    [ObservableProperty] private string _filterInstance = string.Empty;
    [ObservableProperty] private JobStatus? _filterStatus;
    [ObservableProperty] private JobSummary? _selectedJob;
    [ObservableProperty] private BatchComparisonReport? _report;
    [ObservableProperty] private JobResult? _result;
    [ObservableProperty] private double _liveProgress;
    [ObservableProperty] private string? _liveStatus;
    [ObservableProperty] private string? _info;

    public JobsViewModel(ServerSession session, JobProgressHubClient hub, DialogService dialogs)
    {
        _session = session;
        _hub = hub;
        _dialogs = dialogs;
        _hub.ProgressReceived += OnProgress;
    }

    /// <summary>Called via navigation (e.g. from a trigger) to open a specific job after the list loads.</summary>
    public void OpenJob(Guid id) => _pendingJobId = id;

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        try { await _hub.EnsureStartedAsync(); } catch { /* live progress is best-effort */ }
        await LoadJobsAsync();

        if (_pendingJobId is { } id)
        {
            _pendingJobId = null;
            var match = Jobs.FirstOrDefault(j => j.Id == id) ?? await _session.Require().GetJobAsync(id);
            if (match is not null)
            {
                if (Jobs.All(j => j.Id != match.Id)) Jobs.Insert(0, match);
                SelectedJob = Jobs.First(j => j.Id == match.Id);
            }
        }
    });

    private async Task LoadJobsAsync()
    {
        var list = await _session.Require().ListJobsAsync(
            string.IsNullOrWhiteSpace(FilterBranch) ? null : FilterBranch,
            string.IsNullOrWhiteSpace(FilterInstance) ? null : FilterInstance,
            FilterStatus);
        Jobs.Clear();
        foreach (var j in list) Jobs.Add(j);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadJobsAsync);

    partial void OnSelectedJobChanged(JobSummary? value)
    {
        Report = null;
        Result = null;
        Tasks.Clear();
        if (value is null) { LiveStatus = null; LiveProgress = 0; return; }

        LiveProgress = value.Progress;
        LiveStatus = value.Status.ToString();
        _ = RunAsync(async () =>
        {
            await _hub.JoinJobAsync(value.Id);
            await LoadTasksAsync(value.Id);
        });
    }

    private async Task LoadTasksAsync(Guid id)
    {
        Tasks.Clear();
        foreach (var t in await _session.Require().GetTasksAsync(id)) Tasks.Add(t);
    }

    private void OnProgress(JobProgress p)
    {
        if (SelectedJob?.Id != p.JobId) return;
        LiveProgress = p.Progress;
        LiveStatus = p.Status;
        Info = $"{p.Status}: {p.ProcessedCount}/{p.TotalCount}";
    }

    [RelayCommand]
    private Task LoadTasksCmdAsync() => RunAsync(async () => { if (SelectedJob is { } j) await LoadTasksAsync(j.Id); });

    [RelayCommand]
    private Task LoadReportAsync() => RunAsync(async () => { if (SelectedJob is { } j) Report = await _session.Require().GetReportAsync(j.Id); });

    [RelayCommand]
    private Task LoadResultAsync() => RunAsync(async () => { if (SelectedJob is { } j) Result = await _session.Require().GetResultAsync(j.Id); });

    [RelayCommand] private Task PauseAsync() => ActAsync(c => c.PauseJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task ResumeAsync() => ActAsync(c => c.ResumeJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task CancelAsync() => ActAsync(c => c.CancelJobAsync(SelectedJob!.Id));
    [RelayCommand] private Task RetryAsync() => ActAsync(c => c.RetryJobAsync(SelectedJob!.Id));

    private Task ActAsync(Func<DiffPdfClient, Task<JobSummary>> action) => RunAsync(async () =>
    {
        if (SelectedJob is null) throw new InvalidOperationException("Vyber job.");
        var updated = await action(_session.Require());
        ReplaceJob(updated);
        Info = $"Job {updated.Id}: {updated.Status}.";
    });

    private void ReplaceJob(JobSummary updated)
    {
        for (int i = 0; i < Jobs.Count; i++)
        {
            if (Jobs[i].Id == updated.Id) { Jobs[i] = updated; break; }
        }
        SelectedJob = updated;
    }

    [RelayCommand]
    private Task DownloadAsync(FilePairResult? file) => RunAsync(async () =>
    {
        if (SelectedJob is null || file?.HighlightedPdfPath is null)
            throw new InvalidOperationException("Pro tuto dvojici není diff-artefakt.");
        var relativePath = Path.GetFileName(file.HighlightedPdfPath);
        var bytes = await _session.Require().DownloadArtifactAsync(SelectedJob.Id, relativePath);
        var saved = await _dialogs.SaveBytesAsync(relativePath, bytes);
        Info = saved is null ? "Stažení zrušeno." : $"Uloženo: {saved}";
    });
}
