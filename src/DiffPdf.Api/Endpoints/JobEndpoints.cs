using DiffPdf.Application.Jobs;
using DiffPdf.Core.Models;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Job status, tasks, report, CI-gate result, artifacts, cancel and retry. The handlers bind the request,
/// call <see cref="IJobService"/> and map the outcome to HTTP — the lifecycle orchestration (cancel → skip
/// pending + dispatch; pause → publish; resume; retry → requeue + reopen + re-enqueue) lives in DiffPdf.Application.
/// </summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs").WithTags("Jobs");

        group.MapGet("/", async (
            string? branchKey, string? instanceKey, string? status, int? limit, int? offset,
            IJobService jobs, HttpResponse response, CancellationToken ct) =>
        {
            var page = await jobs.ListAsync(new JobFilter(branchKey, instanceKey, status, limit, offset), ct);
            // Non-breaking pagination: the body stays a plain array; the matching total is in a header.
            response.Headers["X-Total-Count"] = page.Total.ToString();
            return Results.Ok(page.Items.Select(JobSummary.From));
        }).WithSummary("List jobs (filter by scope/status; page with limit<=500 + offset; total count in the X-Total-Count header)")
          .Produces<IEnumerable<JobSummary>>();

        group.MapGet("/{id:guid}", async (Guid id, IJobService jobs, CancellationToken ct) =>
            await jobs.GetAsync(id, ct) is { } job ? Results.Ok(JobSummary.From(job)) : Results.NotFound())
            .WithSummary("Get job status + progress").Produces<JobSummary>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/tasks", async (
            Guid id, int? limit, int? offset, string? search, bool? onlyDiffering,
            IJobService jobs, HttpResponse response, CancellationToken ct) =>
        {
            var page = await jobs.ListTasksPagedAsync(
                id, Math.Clamp(limit ?? 200, 1, 1000), Math.Max(0, offset ?? 0),
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(), onlyDiffering ?? false, ct);
            if (page is not { } p) return Results.NotFound();
            // Notification channel parity with the jobs list: the matching total rides the X-Total-Count header.
            response.Headers["X-Total-Count"] = p.Total.ToString();
            return Results.Ok(p.Items.Select(FilePairTaskSummary.From));
        }).WithSummary("List the job's file-pair tasks (paged: limit<=1000 + offset; filter by search/onlyDiffering; total in X-Total-Count)")
            .Produces<IEnumerable<FilePairTaskSummary>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/report", async (Guid id, IJobService jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Report is null)
                return Results.Problem("Report not ready.", statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(job.Report);
        }).WithSummary("Aggregate batch report").Produces<BatchComparisonReport>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/result", async (Guid id, IJobService jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct);
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

        group.MapPost("/{id:guid}/cancel", (Guid id, IJobService jobs, CancellationToken ct) =>
            Run(async () =>
            {
                var job = await jobs.CancelAsync(id, ct);
                return job is null ? Results.NotFound() : Results.Ok(JobSummary.From(job));
            }))
            .WithSummary("Cancel a Draft/queued/running/paused job").Produces<JobSummary>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/pause", (Guid id, IJobService jobs, CancellationToken ct) =>
            Run(async () =>
            {
                var job = await jobs.PauseAsync(id, ct);
                return job is null ? Results.NotFound() : Results.Ok(JobSummary.From(job));
            }))
            .WithSummary("Pause a Running job (in-flight pairs finish; pending pairs wait for resume)")
            .Produces<JobSummary>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/resume", (Guid id, IJobService jobs, CancellationToken ct) =>
            Run(async () =>
            {
                var outcome = await jobs.ResumeAsync(id, ct);
                return outcome is null
                    ? Results.NotFound()
                    : Results.Ok(new { resumed = outcome.Redispatched, job = JobSummary.From(outcome.Job) });
            }))
            .WithSummary("Resume a Paused job (re-dispatches the pending pairs)")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/retry", (Guid id, IJobService jobs, CancellationToken ct) =>
            Run(async () =>
            {
                var outcome = await jobs.RetryAsync(id, ct);
                return outcome is null
                    ? Results.NotFound()
                    : Results.Ok(new { retried = outcome.Retried, job = JobSummary.From(outcome.Job) });
            }))
            .WithSummary("Re-run the failed file-pairs of a finished job").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/artifacts/{**relativePath}", async (
            Guid id, string relativePath, IJobService jobs, CancellationToken ct) =>
        {
            var artifact = await jobs.ResolveArtifactAsync(id, relativePath, ct);
            return artifact.Outcome switch
            {
                ArtifactOutcome.InvalidPath => Results.Problem("Invalid path.", statusCode: StatusCodes.Status400BadRequest),
                ArtifactOutcome.JobNotFound or ArtifactOutcome.FileNotFound => Results.NotFound(),
                _ => Results.File(artifact.AbsolutePath!, "application/pdf", Path.GetFileName(artifact.AbsolutePath!)),
            };
        }).WithSummary("Download a highlighted diff PDF artifact").ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps a job lifecycle conflict (<see cref="JobConflictException"/>) to 409; everything else flows through.</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (JobConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}
