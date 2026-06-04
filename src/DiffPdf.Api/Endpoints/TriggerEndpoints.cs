using System.Security.Claims;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.Triggers;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// On-demand triggers: launch a batch for one instance (webhook style) or fan out across
/// every enabled instance of a branch. Both create + start the job in one call and apply
/// the same readiness gate as the scheduler (skip when there is nothing to compare).
/// </summary>
public static class TriggerEndpoints
{
    public static void MapTriggerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/triggers/{branchKey}/{instanceKey}", async (
            string branchKey, string instanceKey, IBatchLauncher launcher,
            IBranchStore branches, IInstanceStore instances, IScopeConfigurationResolver resolver, CancellationToken ct) =>
        {
            var spec = await ResolveInstanceSpecAsync(branches, instances, resolver, branchKey, instanceKey, ct);
            var result = await launcher.LaunchAsync(branchKey, instanceKey, spec, ct: ct);
            var body = new TriggerResult(result.Outcome.ToString(), result.JobId, result.Detail);
            return result.Outcome switch
            {
                LaunchOutcome.Launched => Results.Accepted($"/api/v1/jobs/{result.JobId}", body),
                LaunchOutcome.ScopeNotFound => Results.Problem(result.Detail, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Ok(body), // NothingToCompare / Unreachable — accepted but nothing launched
            };
        })
        .WithTags("Triggers")
        .WithSummary("Trigger a batch for one instance now (create + start; skips when nothing to compare)")
        .Produces<TriggerResult>(StatusCodes.Status202Accepted)
        .Produces<TriggerResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireRateLimiting("expensive");

        app.MapPost("/branches/{branchKey}/run", async (
            string branchKey, IBranchStore branches, IInstanceStore instances, IBatchLauncher launcher,
            IScopeConfigurationResolver resolver, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null)
                return Results.Problem($"Branch '{branchKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            var list = await instances.ListAsync(branch.Id, ct);
            var results = new List<InstanceRunResult>();
            foreach (var instance in list.Where(i => i.Enabled))
            {
                // Each instance launches with its own effective (inherited) configuration.
                var eff = await resolver.ResolveForInstanceAsync(branch.Id, instance.Id, ct);
                var r = await launcher.LaunchAsync(branchKey, instance.Key, LaunchSpec.FromEffective(eff), ct: ct);
                results.Add(new InstanceRunResult(instance.Key, r.Outcome.ToString(), r.JobId, r.Detail));
            }

            int launched = results.Count(r => r.JobId is not null);
            return Results.Ok(new BranchRunResult(branchKey, launched, results.Count - launched, results));
        })
        .WithTags("Triggers")
        .WithSummary("Trigger a batch for every enabled instance under a branch (fan-out)")
        .Produces<BranchRunResult>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireRateLimiting("expensive");

        // ---------------- Managed trigger entities ----------------
        var group = app.MapGroup("/triggers").WithTags("Triggers");

        group.MapGet("/", async (
            Guid? instanceId, Guid? branchId, TriggerStatus? status, bool? includeDeleted, ITriggerStore store, CancellationToken ct) =>
            Results.Ok((await store.ListAsync(new TriggerQuery
            {
                InstanceId = instanceId, BranchId = branchId, Status = status, IncludeDeleted = includeDeleted ?? false,
            }, ct)).Select(TriggerResponse.From)))
            .WithSummary("List triggers (filter by instance/branch/status)").Produces<IEnumerable<TriggerResponse>>();

        group.MapGet("/{triggerId:guid}", async (Guid triggerId, ITriggerStore store, CancellationToken ct) =>
            await store.GetAsync(triggerId, ct) is { } t ? Results.Ok(TriggerResponse.From(t)) : Results.NotFound())
            .WithSummary("Get a trigger").Produces<TriggerResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (CreateTriggerRequest req, ITriggerService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var t = await svc.CreateAsync(new CreateTriggerInput
                {
                    BranchKey = req.BranchKey, InstanceKey = req.InstanceKey, Name = req.Name,
                    Description = req.Description, Enabled = req.Enabled, Spec = req.Spec ?? new TriggerSpec(),
                }, Actor(user), JobSource.RestApi, ct);
                return Results.Created($"/api/v1/triggers/{t.Id}", TriggerResponse.From(t));
            }
            catch (TriggerValidationException ex)
            {
                return Results.Json(new TriggerErrorResponse(false, ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithSummary("Create a trigger").Produces<TriggerResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPatch("/{triggerId:guid}", async (Guid triggerId, UpdateTriggerRequest req, ITriggerService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var t = await svc.UpdateAsync(triggerId, new UpdateTriggerInput
                {
                    Name = req.Name, Description = req.Description, Enabled = req.Enabled, Spec = req.Spec, ExpectedVersion = req.Version,
                }, Actor(user), ct);
                return t is null ? Results.NotFound() : Results.Ok(TriggerResponse.From(t));
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update a trigger (optimistic concurrency via Version; history preserved)")
          .Produces<TriggerResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{triggerId:guid}", async (Guid triggerId, ITriggerService svc, ClaimsPrincipal user, CancellationToken ct) =>
            await svc.DeleteAsync(triggerId, Actor(user), JobSource.RestApi, ct) is null ? Results.NotFound() : Results.NoContent())
            .WithSummary("Soft-delete a trigger (run history, jobs and results are preserved)")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{triggerId:guid}/enable", async (Guid triggerId, ITriggerService svc, ClaimsPrincipal user, CancellationToken ct) =>
            await svc.SetEnabledAsync(triggerId, true, Actor(user), JobSource.RestApi, ct) is { } t ? Results.Ok(TriggerResponse.From(t)) : Results.NotFound())
            .WithSummary("Enable a trigger").Produces<TriggerResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{triggerId:guid}/disable", async (Guid triggerId, ITriggerService svc, ClaimsPrincipal user, CancellationToken ct) =>
            await svc.SetEnabledAsync(triggerId, false, Actor(user), JobSource.RestApi, ct) is { } t ? Results.Ok(TriggerResponse.From(t)) : Results.NotFound())
            .WithSummary("Disable a trigger").Produces<TriggerResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{triggerId:guid}/run", async (Guid triggerId, HttpRequest http, ITriggerService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            string? idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault();
            var r = await svc.RunAsync(triggerId, JobSource.RestApi, Actor(user), idempotencyKey, ct);
            var body = new RunTriggerResponse(r.Success, r.TriggerId, r.BatchJobId, r.Status, r.Message, r.ErrorCode);
            if (r.Success) return Results.Accepted($"/api/v1/jobs/{r.BatchJobId}", body);
            return r.ErrorCode switch
            {
                "TRIGGER_NOT_FOUND" => Results.Json(body, statusCode: StatusCodes.Status404NotFound),
                "TRIGGER_DISABLED" or "TRIGGER_DELETED" => Results.Json(body, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(body, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }).WithSummary("Run a trigger now: creates + enqueues a batch job and returns its id (never waits). Supports Idempotency-Key.")
          .Produces<RunTriggerResponse>(StatusCodes.Status202Accepted)
          .RequireRateLimiting("expensive");

        group.MapGet("/{triggerId:guid}/runs", async (Guid triggerId, int? limit, ITriggerRunStore runs, CancellationToken ct) =>
            Results.Ok((await runs.ListByTriggerAsync(triggerId, limit is > 0 ? limit.Value : 50, ct)).Select(TriggerRunResponse.From)))
            .WithSummary("List a trigger's runs (newest first)").Produces<IEnumerable<TriggerRunResponse>>();

        group.MapGet("/{triggerId:guid}/jobs", async (Guid triggerId, IJobStore jobs, CancellationToken ct) =>
            Results.Ok((await jobs.ListAsync(new JobListQuery { TriggerId = triggerId, Limit = 200 }, ct)).Select(JobSummary.From)))
            .WithSummary("List batch jobs created by a trigger").Produces<IEnumerable<JobSummary>>();

        group.MapGet("/{triggerId:guid}/audit", async (Guid triggerId, IAuditLogStore audit, CancellationToken ct) =>
            Results.Ok((await audit.ListAsync("Trigger", triggerId.ToString(), 100, ct)).Select(AuditEntryResponse.From)))
            .WithSummary("List a trigger's audit history").Produces<IEnumerable<AuditEntryResponse>>();

        app.MapGet("/instances/{instanceId:guid}/triggers", async (Guid instanceId, ITriggerStore store, CancellationToken ct) =>
            Results.Ok((await store.ListAsync(new TriggerQuery { InstanceId = instanceId }, ct)).Select(TriggerResponse.From)))
            .WithTags("Triggers").WithSummary("List an instance's triggers").Produces<IEnumerable<TriggerResponse>>();

        app.MapGet("/branches/{branchId:guid}/triggers", async (Guid branchId, ITriggerStore store, CancellationToken ct) =>
            Results.Ok((await store.ListAsync(new TriggerQuery { BranchId = branchId }, ct)).Select(TriggerResponse.From)))
            .WithTags("Triggers").WithSummary("List a branch's triggers").Produces<IEnumerable<TriggerResponse>>();
    }

    /// <summary>
    /// Builds the launch spec for an on-demand instance run from its resolved effective configuration. Falls
    /// back to defaults when the branch/instance is unknown — the launcher then reports the not-found outcome.
    /// </summary>
    private static async Task<LaunchSpec> ResolveInstanceSpecAsync(
        IBranchStore branches, IInstanceStore instances, IScopeConfigurationResolver resolver,
        string branchKey, string instanceKey, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return LaunchSpec.Default;
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null) return LaunchSpec.Default;
        var eff = await resolver.ResolveForInstanceAsync(branch.Id, instance.Id, ct);
        return LaunchSpec.FromEffective(eff);
    }

    /// <summary>The acting principal for audit (token client id / subject), or null when unauthenticated.</summary>
    private static string? Actor(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirst("client_id")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name
            : null;
}
