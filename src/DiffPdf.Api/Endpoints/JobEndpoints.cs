using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Job status, report, CI-gate result and artifact download.</summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapGet("/api/jobs", async (
            string? businessInstanceKey, string? projectKey, string? status,
            IJobStore jobStore, CancellationToken ct) =>
        {
            JobStatus? parsed = Enum.TryParse<JobStatus>(status, true, out var s) ? s : null;
            var query = new JobListQuery
            {
                BusinessInstanceKey = businessInstanceKey,
                ProjectKey = projectKey,
                Status = parsed,
            };
            var jobs = await jobStore.ListAsync(query, ct);
            return Results.Ok(jobs.Select(JobSummary.From));
        });

        app.MapGet("/api/jobs/{id:guid}", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(JobSummary.From(job));
        });

        app.MapGet("/api/jobs/{id:guid}/report", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Report is null)
                return Results.Json(new { status = job.Status.ToString(), message = "Report not ready." }, statusCode: 409);
            return Results.Ok(job.Report);
        });

        app.MapGet("/api/jobs/{id:guid}/result", async (Guid id, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Report is null)
                return Results.Json(new { status = job.Status.ToString(), message = "Report not ready." }, statusCode: 409);

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
            return report.Passed ? Results.Ok(payload) : Results.Json(payload, statusCode: 422);
        });

        app.MapGet("/api/jobs/{id:guid}/artifacts/{**relativePath}", async (
            Guid id, string relativePath, IJobStore jobStore, IJobStoragePathProvider paths, CancellationToken ct) =>
        {
            var job = await jobStore.GetAsync(id, ct);
            if (job is null) return Results.NotFound();

            string artifactsRoot = Path.GetFullPath(paths.GetArtifactsPath(job));
            string requested = Path.GetFullPath(Path.Combine(artifactsRoot, relativePath));
            if (!requested.StartsWith(artifactsRoot, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid path." });
            if (!File.Exists(requested))
                return Results.NotFound();

            return Results.File(requested, "application/pdf", Path.GetFileName(requested));
        });
    }
}
