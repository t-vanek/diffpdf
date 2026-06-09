using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Jobs;

/// <summary>Filter for the job list (raw query values; the service parses/clamps them into a query).</summary>
public sealed record JobFilter(string? BranchKey, string? InstanceKey, string? Status, int? Limit, int? Offset);

/// <summary>A page of job summaries plus the total matching the filter (for the X-Total-Count header).</summary>
public sealed record JobListPage(IReadOnlyList<JobListItem> Items, int Total);

/// <summary>Result of resuming a paused job: the job plus how many pending pairs were re-dispatched.</summary>
public sealed record JobResumeOutcome(ComparisonJob Job, int Redispatched);

/// <summary>Result of retrying a finished job: how many failed pairs were requeued plus the reopened job.</summary>
public sealed record JobRetryOutcome(int Retried, ComparisonJob Job);

/// <summary>Outcome of resolving an artifact download path (kept transport-agnostic; the endpoint serves the file).</summary>
public enum ArtifactOutcome { JobNotFound, InvalidPath, FileNotFound, Ok }

/// <summary>A resolved artifact request: the outcome and, when <see cref="ArtifactOutcome.Ok"/>, the validated absolute path.</summary>
public sealed record ArtifactResult(ArtifactOutcome Outcome, string? AbsolutePath = null);

/// <summary>
/// Job queries and lifecycle actions. The lifecycle methods carry the full orchestration that used to live in
/// the endpoints (cancel → skip pending + dispatch; pause → publish progress; resume; retry → requeue failed +
/// reopen + re-enqueue). Job-not-found is a null return; a wrong-state action raises <see cref="JobConflictException"/>.
/// </summary>
public interface IJobService
{
    Task<JobListPage> ListAsync(JobFilter filter, CancellationToken ct = default);
    Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FilePairTask>?> ListTasksAsync(Guid id, CancellationToken ct = default);

    /// <summary>A page of a job's file-pair tasks (optional name <c>search</c> + <c>onlyDiffering</c> filter); null
    /// if the job is unknown. Returns the page plus the filtered total — for the client's lazy-loaded file list.</summary>
    Task<(IReadOnlyList<FilePairTask> Items, int Total)?> ListTasksPagedAsync(
        Guid id, int limit, int offset, string? search, bool onlyDiffering, CancellationToken ct = default);

    Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default);
    Task<ComparisonJob?> PauseAsync(Guid id, CancellationToken ct = default);
    Task<JobResumeOutcome?> ResumeAsync(Guid id, CancellationToken ct = default);
    Task<JobRetryOutcome?> RetryAsync(Guid id, CancellationToken ct = default);
    Task<ArtifactResult> ResolveArtifactAsync(Guid id, string relativePath, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class JobService(
    IJobStore jobStore,
    IFilePairTaskStore taskStore,
    IBranchQueueDispatcher dispatcher,
    IJobProgressPublisher progress,
    IJobResumeService resume,
    IFilePairRequeueDispatcher requeue,
    IJobStoragePathProvider paths) : IJobService
{
    public async Task<JobListPage> ListAsync(JobFilter filter, CancellationToken ct = default)
    {
        JobStatus? parsed = Enum.TryParse<JobStatus>(filter.Status, true, out var s) ? s : null;
        var query = new JobListQuery
        {
            BranchKey = filter.BranchKey,
            InstanceKey = filter.InstanceKey,
            Status = parsed,
            Limit = Math.Clamp(filter.Limit ?? 100, 1, 500),
            Offset = Math.Max(0, filter.Offset ?? 0),
        };
        // Lightweight projection: scalar columns + denormalized verdict, no request/report JSON deserialized.
        var items = await jobStore.ListSummariesAsync(query, ct);
        var total = await jobStore.CountAsync(query, ct);
        return new JobListPage(items, total);
    }

    public async Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default) =>
        await jobStore.GetAsync(id, ct);

    public async Task<IReadOnlyList<FilePairTask>?> ListTasksAsync(Guid id, CancellationToken ct = default)
    {
        if (await jobStore.GetAsync(id, ct) is null) return null;
        return await taskStore.ListByJobAsync(id, ct);
    }

    public async Task<(IReadOnlyList<FilePairTask> Items, int Total)?> ListTasksPagedAsync(
        Guid id, int limit, int offset, string? search, bool onlyDiffering, CancellationToken ct = default)
    {
        if (await jobStore.GetAsync(id, ct) is null) return null;
        return await taskStore.ListByJobPagedAsync(id, limit, offset, search, onlyDiffering, ct);
    }

    public async Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default)
    {
        if (await jobStore.GetAsync(id, ct) is null) return null;
        var cancelled = await jobStore.CancelAsync(id, ct);
        if (cancelled is null)
            throw new JobConflictException("Job is not in a cancellable state.");
        // Cancellation leaves un-started pairs Queued; skip them so they don't linger as pending comparisons.
        await taskStore.SkipPendingForJobAsync(id, ct);
        // Cancelling frees the branch — release the next pending run.
        await dispatcher.DispatchBranchAsync(cancelled.BranchId, ct);
        return cancelled;
    }

    public async Task<ComparisonJob?> PauseAsync(Guid id, CancellationToken ct = default)
    {
        if (await jobStore.GetAsync(id, ct) is null) return null;
        var paused = await jobStore.PauseAsync(id, ct);
        if (paused is null)
            throw new JobConflictException("Only a Running job can be paused.");
        await progress.PublishAsync(JobProgressChanged.From(paused), ct);
        return paused;
    }

    public async Task<JobResumeOutcome?> ResumeAsync(Guid id, CancellationToken ct = default)
    {
        if (await jobStore.GetAsync(id, ct) is null) return null;
        var (resumed, redispatched) = await resume.ResumeAsync(id, ct);
        if (resumed is null)
            throw new JobConflictException("Only a Paused job can be resumed.");
        return new JobResumeOutcome(resumed, redispatched);
    }

    public async Task<JobRetryOutcome?> RetryAsync(Guid id, CancellationToken ct = default)
    {
        var job = await jobStore.GetAsync(id, ct);
        if (job is null) return null;
        if (job.Status is not (JobStatus.Completed or JobStatus.Failed))
            throw new JobConflictException("Only a finished (Completed/Failed) job can be retried.");

        var tasks = await taskStore.ListByJobAsync(id, ct);
        var failed = tasks.Where(t =>
            t.Status == FilePairTaskStatus.Failed ||
            (t.Status == FilePairTaskStatus.Completed && t.Result?.Status == FilePairStatus.Error)).ToList();

        if (failed.Count == 0)
            return new JobRetryOutcome(0, job);

        foreach (var t in failed)
            await taskStore.RequeueForRetryAsync(t.Id, ct);

        int processed = Math.Max(0, job.TotalCount - failed.Count);
        var reopened = await jobStore.ReopenAsync(id, processed, ct);
        if (reopened is null)
            throw new JobConflictException("Job could not be reopened for retry.");

        foreach (var t in failed)
            await requeue.RequeueAsync(id, t.Id, ct);

        return new JobRetryOutcome(failed.Count, reopened);
    }

    public async Task<ArtifactResult> ResolveArtifactAsync(Guid id, string relativePath, CancellationToken ct = default)
    {
        var job = await jobStore.GetAsync(id, ct);
        if (job is null) return new ArtifactResult(ArtifactOutcome.JobNotFound);

        string artifactsRoot = Path.GetFullPath(paths.GetArtifactsPath(job));
        string requested = Path.GetFullPath(Path.Combine(artifactsRoot, relativePath));
        // Containment check defeats path traversal (e.g. ../../etc) — the resolved path must stay under the root.
        if (!requested.StartsWith(artifactsRoot, StringComparison.Ordinal))
            return new ArtifactResult(ArtifactOutcome.InvalidPath);
        if (!File.Exists(requested))
            return new ArtifactResult(ArtifactOutcome.FileNotFound);

        return new ArtifactResult(ArtifactOutcome.Ok, requested);
    }
}
