using System.IO.Compression;
using DiffPdf.Application.Jobs;
using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Job status, tasks, report, CI-gate result, artifacts, cancel, retry and delete. The handlers bind the request,
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

        group.MapGet("/{id:guid}/report", async (
            Guid id, IJobService jobs, HttpRequest request, HttpResponse response, CancellationToken ct) =>
        {
            var report = await jobs.GetReportRawAsync(id, ct);
            if (report is null) return Results.NotFound();
            if (report.ReportJson is null)
                return Results.Problem("Report not ready.", statusCode: StatusCodes.Status409Conflict);

            // A finished report is immutable, so the job version makes a stable ETag: pollers revalidate with
            // If-None-Match and a 304 skips both the multi-MB body and any serialization work.
            string etag = $"\"{id:N}-{report.Version}\"";
            response.Headers.ETag = etag;
            if (MatchesIfNoneMatch(request.Headers.IfNoneMatch, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            // The stored JSON is written with the same Web-defaults serializer the HTTP layer uses (DiffPdfJson),
            // so it streams back verbatim — no deserialize→re-serialize round-trip for the biggest payload.
            return Results.Content(report.ReportJson, "application/json; charset=utf-8");
        }).WithSummary("Aggregate batch report (ETag/If-None-Match supported)")
          .Produces<BatchComparisonReport>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

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

        group.MapDelete("/{id:guid}", (Guid id, IJobService jobs, CancellationToken ct) =>
            Run(async () => await jobs.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()))
            .WithSummary("Permanently delete a finished (Completed/Cancelled) job incl. its tasks, report and artifacts")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/send", async (
            Guid id, SendJobDiffsRequest request, IJobService jobs, EmailSettingsResolver resolver,
            IEmailSender sender, IOptions<NotificationOptions> notificationOptions, CancellationToken ct) =>
        {
            var recipients = (request.Recipients ?? [])
                .Select(r => r?.Trim()).Where(r => !string.IsNullOrEmpty(r)).Cast<string>().Distinct().ToList();
            if (recipients.Count == 0)
                return Results.Problem("At least one recipient is required.", statusCode: StatusCodes.Status400BadRequest);

            var job = await jobs.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Report is null)
                return Results.Problem("Report not ready.", statusCode: StatusCodes.Status409Conflict);

            var settings = await resolver.ResolveAsync(ct);
            if (settings is not { IsConfigured: true })
                return Results.Problem("SMTP is not configured. Save the e-mail settings first.",
                    statusCode: StatusCodes.Status400BadRequest);

            var plan = DiffMailPlanner.Plan(job.Report, request.Files);
            if (plan.UnknownFiles.Count > 0)
                return Results.Problem($"Unknown file pair(s): {string.Join(", ", plan.UnknownFiles)}.",
                    statusCode: StatusCodes.Status400BadRequest);

            // Re-resolve every artifact through the job's containment check; one pruned by retention since the
            // report was written degrades to "skipped" instead of failing the whole send.
            var skipped = plan.SkippedFiles.ToList();
            var attached = new List<(DiffMailItem Item, string AbsolutePath, long Bytes)>();
            foreach (var item in plan.Items)
            {
                var artifact = await jobs.ResolveArtifactAsync(id, item.ArtifactFileName, ct);
                if (artifact.Outcome != ArtifactOutcome.Ok) skipped.Add(item.Pair.RelativePath);
                else attached.Add((item, artifact.AbsolutePath!, new FileInfo(artifact.AbsolutePath!).Length));
            }
            if (attached.Count == 0)
                return Results.Problem("There is no differing pair with a diff PDF to send.",
                    statusCode: StatusCodes.Status400BadRequest);

            long totalBytes = attached.Sum(a => a.Bytes);
            bool zip = DiffMailPlanner.ShouldZip(request.Zip, attached.Count, totalBytes);
            long maxBytes = notificationOptions.Value.MaxMailAttachmentMb * 1024L * 1024L;

            string? tempZip = null;
            try
            {
                IReadOnlyList<EmailAttachment> attachments;
                if (zip)
                {
                    tempZip = Path.Combine(Path.GetTempPath(), $"diffpdf-send-{Guid.NewGuid():N}.zip");
                    using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
                        foreach (var a in attached)
                            archive.CreateEntryFromFile(a.AbsolutePath, a.Item.ZipEntryName);
                    totalBytes = new FileInfo(tempZip).Length;
                    attachments = [new EmailAttachment($"diffpdf-{job.BranchKey}-{job.InstanceKey}.zip", tempZip)];
                }
                else
                {
                    // The artifact names are flat — prefix the pair's subfolder so two same-named reports from
                    // different folders stay distinguishable in the mail client.
                    attachments = attached
                        .Select(a => new EmailAttachment(a.Item.ZipEntryName.Replace('/', '_'), a.AbsolutePath))
                        .ToList();
                }

                if (maxBytes > 0 && totalBytes > maxBytes)
                    return Results.Problem(
                        $"Attachments total {totalBytes / (1024.0 * 1024.0):0.0} MB, which exceeds the " +
                        $"{notificationOptions.Value.MaxMailAttachmentMb} MB limit (Notifications:MaxMailAttachmentMb). " +
                        "Send fewer pairs, or download the diffs via the artifacts endpoint.",
                        statusCode: StatusCodes.Status413PayloadTooLarge);

                string subject = attached.Count == 1
                    ? $"DiffPdf — {job.BranchKey}/{job.InstanceKey}: odlišné PDF {attached[0].Item.Pair.RelativePath}"
                    : $"DiffPdf — {job.BranchKey}/{job.InstanceKey}: {attached.Count}× odlišné PDF";
                var finalPlan = new DiffMailPlan(attached.Select(a => a.Item).ToList(), skipped, []);
                string body = DiffMailPlanner.ComposeBody(job, finalPlan, request.Note, zip,
                    notificationOptions.Value.JobLink(id));

                try
                {
                    await sender.SendAsync(settings, recipients, subject, body, attachments, ct);
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Odeslání selhalo: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
                }
                return Results.Ok(new SendJobDiffsResponse(recipients.Count, attached.Count, zip, totalBytes, skipped));
            }
            finally
            {
                if (tempZip is not null)
                    try { File.Delete(tempZip); } catch (IOException) { /* best-effort temp cleanup */ }
            }
        }).WithSummary("E-mail the highlighted diff PDFs of one pair, a chosen set, or every differing pair (auto-ZIP)")
          .Produces<SendJobDiffsResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
          .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/{id:guid}/artifacts/{**relativePath}", async (
            Guid id, string relativePath, IJobService jobs, CancellationToken ct) =>
        {
            var artifact = await jobs.ResolveArtifactAsync(id, relativePath, ct);
            return artifact.Outcome switch
            {
                ArtifactOutcome.InvalidPath => Results.Problem("Invalid path.", statusCode: StatusCodes.Status400BadRequest),
                ArtifactOutcome.JobNotFound or ArtifactOutcome.FileNotFound => Results.NotFound(),
                _ => Results.File(artifact.AbsolutePath!, "application/pdf", Path.GetFileName(artifact.AbsolutePath!),
                    enableRangeProcessing: true),
            };
        }).WithSummary("Download a highlighted diff PDF artifact").ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps a job lifecycle conflict (<see cref="JobConflictException"/>) to 409; everything else flows through.</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (JobConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }

    /// <summary>True when any entity-tag in the If-None-Match header equals <paramref name="etag"/> (we only emit strong tags).</summary>
    private static bool MatchesIfNoneMatch(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string etag)
    {
        foreach (string? raw in ifNoneMatch)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (var candidate in raw.Split(','))
                if (candidate.Trim() == etag) return true;
        }
        return false;
    }
}
