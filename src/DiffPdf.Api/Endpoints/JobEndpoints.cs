using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Wolverine;

namespace DiffPdf.Api.Endpoints;

/// <summary>Job status, tasks, report, CI-gate result, artifacts, cancel and retry.</summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs").WithTags("Jobs");

        group.MapGet("/", async (
            string? branchKey, string? instanceKey, string? status, int? limit, int? offset,
            IJobStore jobStore, HttpResponse response, CancellationToken ct) =>
        {
            JobStatus? parsed = Enum.TryParse<JobStatus>(status, true, out var s) ? s : null;
            var query = new JobListQuery
            {
                BranchKey = branchKey,
                InstanceKey = instanceKey,
                Status = parsed,
                Limit = Math.Clamp(limit ?? 100, 1, 500),
                Offset = Math.Max(0, offset ?? 0),
            };
            var jobs = await jobStore.ListAsync(query, ct);
            // Non-breaking pagination: the body stays a plain array; the matching total is in a header.
            response.Headers["X-Total-Count"] = (await jobStore.CountAsync(query, ct)).ToString();
            return Results.Ok(jobs.Select(JobSummary.From));
        }).WithSummary("List jobs (filter by scope/status; page with limit<=500 + offset; total count in the X-Total-Count header)")
          .Produces<IEnumerable<JobSummary>>();

        group.MapGet("/{id:guid}", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(JobSummary.From(job));
        }).WithSummary("Get job status + progress").Produces<JobSummary>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/tasks", async (Guid id, IJobStore jobStore, IFilePairTaskStore taskStore, CancellationToken ct) =>
        {
            if (await jobStore.GetAsync(id, ct) is null) return Results.NotFound();
            var tasks = await taskStore.ListByJobAsync(id, ct);
            return Results.Ok(tasks.Select(FilePairTaskSummary.From));
        }).WithSummary("List the job's file-pair tasks").Produces<IEnumerable<FilePairTaskSummary>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/report", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Report is null)
                return Results.Problem("Report not ready.", statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(job.Report);
        }).WithSummary("Aggregate batch report").Produces<BatchComparisonReport>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/result", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Report is null)
                return Results.Problem("Report not ready.", statusCode: StatusCodes.Status409Conflict);

            var report = job.Report;
            var payload = new
            {
                passed = report.Passed,
                gated = report.Gate is not null,
                violations = report.GateViolations,
                summary = new
                {
                    report.Total, report.Identical, report.Differing,
                    report.OnlyInOld, report.OnlyInNew, report.Errors, report.FilesWithContentErrors,
                },
            };
            return report.Passed ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status422UnprocessableEntity);
        }).WithSummary("CI gate verdict (200 pass / 422 fail)").ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/cancel", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            if (await jobStore.GetAsync(id, ct) is null) return Results.NotFound();
            var cancelled = await jobStore.CancelAsync(id, ct);
            return cancelled is null
                ? Results.Problem("Job is not in a cancellable state.", statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(JobSummary.From(cancelled));
        }).WithSummary("Cancel a Draft/queued/running/paused job").Produces<JobSummary>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/pause", async (
            Guid id, IJobStore jobStore, IJobProgressPublisher progress, CancellationToken ct) =>
        {
            if (await jobStore.GetAsync(id, ct) is null) return Results.NotFound();
            var paused = await jobStore.PauseAsync(id, ct);
            if (paused is null)
                return Results.Problem("Only a Running job can be paused.", statusCode: StatusCodes.Status409Conflict);

            await progress.PublishAsync(JobProgressChanged.From(paused), ct);
            return Results.Ok(JobSummary.From(paused));
        }).WithSummary("Pause a Running job (in-flight pairs finish; pending pairs wait for resume)")
          .Produces<JobSummary>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/resume", async (
            Guid id, IJobStore jobStore, IFilePairTaskStore taskStore,
            IJobProgressPublisher progress, IMessageBus bus, CancellationToken ct) =>
        {
            if (await jobStore.GetAsync(id, ct) is null) return Results.NotFound();
            var resumed = await jobStore.ResumeAsync(id, ct);
            if (resumed is null)
                return Results.Problem("Only a Paused job can be resumed.", statusCode: StatusCodes.Status409Conflict);

            // Re-dispatch the pending (Queued) pairs. If none remain — everything finished
            // while paused — finalize directly, since nothing else would trigger it.
            var tasks = await taskStore.ListByJobAsync(id, ct);
            var pending = tasks.Where(t => t.Status == FilePairTaskStatus.Queued).ToList();
            foreach (var t in pending)
                await bus.PublishAsync(new CompareFilePair(id, t.Id));
            if (pending.Count == 0)
                await bus.PublishAsync(new FinalizeBatch(id));

            await progress.PublishAsync(JobProgressChanged.From(resumed), ct);
            return Results.Ok(new { resumed = pending.Count, job = JobSummary.From(resumed) });
        }).WithSummary("Resume a Paused job (re-dispatches the pending pairs)")
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/retry", async (
            Guid id, IJobStore jobStore, IFilePairTaskStore taskStore, IMessageBus bus, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is not (JobStatus.Completed or JobStatus.Failed))
                return Results.Problem("Only a finished (Completed/Failed) job can be retried.", statusCode: StatusCodes.Status409Conflict);

            var tasks = await taskStore.ListByJobAsync(id, ct);
            var failed = tasks.Where(t =>
                t.Status == FilePairTaskStatus.Failed ||
                (t.Status == FilePairTaskStatus.Completed && t.Result?.Status == FilePairStatus.Error)).ToList();

            if (failed.Count == 0)
                return Results.Ok(new { retried = 0, job = JobSummary.From(job) });

            foreach (var t in failed)
                await taskStore.RequeueForRetryAsync(t.Id, ct);

            int processed = Math.Max(0, job.TotalCount - failed.Count);
            var reopened = await jobStore.ReopenAsync(id, processed, ct);
            if (reopened is null)
                return Results.Problem("Job could not be reopened for retry.", statusCode: StatusCodes.Status409Conflict);

            foreach (var t in failed)
                await bus.PublishAsync(new CompareFilePair(id, t.Id));

            return Results.Ok(new { retried = failed.Count, job = JobSummary.From(reopened) });
        }).WithSummary("Re-run the failed file-pairs of a finished job").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/artifacts/{**relativePath}", async (
            Guid id, string relativePath, IJobStore jobStore, IJobStoragePathProvider paths, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();

            string artifactsRoot = Path.GetFullPath(paths.GetArtifactsPath(job));
            string requested = Path.GetFullPath(Path.Combine(artifactsRoot, relativePath));
            if (!requested.StartsWith(artifactsRoot, StringComparison.Ordinal))
                return Results.Problem("Invalid path.", statusCode: StatusCodes.Status400BadRequest);
            if (!File.Exists(requested))
                return Results.NotFound();

            return Results.File(requested, "application/pdf", Path.GetFileName(requested));
        }).WithSummary("Download a highlighted diff PDF artifact").ProducesProblem(StatusCodes.Status404NotFound);
    }
}
