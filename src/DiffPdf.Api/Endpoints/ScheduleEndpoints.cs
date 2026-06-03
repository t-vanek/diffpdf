using Cronos;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed recurring schedules, nested under an instance. Creating one arms the
/// DB-backed scheduler (picked up within a tick, no restart); <c>POST .../run</c> launches
/// the batch on demand using the schedule's own options + gate.
/// </summary>
public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/branches/{branchKey}/instances/{instanceKey}/schedules").WithTags("Schedules");

        group.MapPost("/", async (
            string branchKey, string instanceKey, CreateScheduleRequest request,
            IBranchStore branches, IInstanceStore instances, IScheduleStore schedules, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid schedule key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);
            if (!TryValidateCron(request.Cron, out string? cronError))
                return Results.Problem(cronError, statusCode: StatusCodes.Status400BadRequest);

            var (branch, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (branch is null || instance is null) return Results.NotFound();

            var schedule = new ComparisonSchedule
            {
                Id = Guid.NewGuid(),
                BranchId = branch.Id,
                InstanceId = instance.Id,
                BranchKey = branch.Key,
                InstanceKey = instance.Key,
                Key = request.Key,
                Name = string.IsNullOrWhiteSpace(request.Name) ? request.Key : request.Name,
                Cron = request.Cron,
                Options = request.Options,
                Gate = request.Gate,
                SearchPattern = request.SearchPattern,
                Recursive = request.Recursive,
                MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
                Enabled = request.Enabled,
            };
            try
            {
                var created = await schedules.CreateAsync(schedule, ct);
                return Results.Created(
                    $"/api/v1/branches/{branchKey}/instances/{instanceKey}/schedules/{created.Key}",
                    ScheduleResponse.From(created));
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Create a schedule (arms the scheduler; runs the instance on its cron with the given options/gate)")
          .Produces<ScheduleResponse>(StatusCodes.Status201Created)
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (
            string branchKey, string instanceKey,
            IBranchStore branches, IInstanceStore instances, IScheduleStore schedules, CancellationToken ct) =>
        {
            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            var list = await schedules.ListByInstanceAsync(instance.Id, ct);
            return Results.Ok(list.Select(ScheduleResponse.From));
        }).WithSummary("List the instance's schedules").Produces<IEnumerable<ScheduleResponse>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{scheduleKey}", async (
            string branchKey, string instanceKey, string scheduleKey,
            IBranchStore branches, IInstanceStore instances, IScheduleStore schedules, CancellationToken ct) =>
        {
            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            var schedule = await schedules.GetByKeyAsync(instance.Id, scheduleKey, ct);
            return schedule is null ? Results.NotFound() : Results.Ok(ScheduleResponse.From(schedule));
        }).WithSummary("Get a schedule").Produces<ScheduleResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{scheduleKey}", async (
            string branchKey, string instanceKey, string scheduleKey, UpdateScheduleRequest request,
            IBranchStore branches, IInstanceStore instances, IScheduleStore schedules, CancellationToken ct) =>
        {
            if (!TryValidateCron(request.Cron, out string? cronError))
                return Results.Problem(cronError, statusCode: StatusCodes.Status400BadRequest);

            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            var existing = await schedules.GetByKeyAsync(instance.Id, scheduleKey, ct);
            if (existing is null) return Results.NotFound();

            var updated = existing with
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name,
                Cron = request.Cron,
                Options = request.Options,
                Gate = request.Gate,
                SearchPattern = request.SearchPattern,
                Recursive = request.Recursive,
                MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
                Enabled = request.Enabled,
            };
            try
            {
                var saved = await schedules.UpdateAsync(updated, request.Version, ct);
                return Results.Ok(ScheduleResponse.From(saved));
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update a schedule (optimistic concurrency via Version)")
          .Produces<ScheduleResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{scheduleKey}", async (
            string branchKey, string instanceKey, string scheduleKey,
            IBranchStore branches, IInstanceStore instances, IScheduleStore schedules, CancellationToken ct) =>
        {
            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            var schedule = await schedules.GetByKeyAsync(instance.Id, scheduleKey, ct);
            if (schedule is null) return Results.NotFound();
            await schedules.DeleteAsync(schedule.Id, ct);
            return Results.NoContent();
        }).WithSummary("Delete a schedule (stops future runs; in-flight jobs are unaffected)")
          .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{scheduleKey}/run", async (
            string branchKey, string instanceKey, string scheduleKey,
            IBranchStore branches, IInstanceStore instances, IScheduleStore schedules,
            IBatchLauncher launcher, CancellationToken ct) =>
        {
            var (_, instance) = await ResolveAsync(branchKey, instanceKey, branches, instances, ct);
            if (instance is null) return Results.NotFound();
            var schedule = await schedules.GetByKeyAsync(instance.Id, scheduleKey, ct);
            if (schedule is null) return Results.NotFound();

            // Same pre-flight gate the removed POST /jobs/{id}/start used to enforce.
            var result = await launcher.LaunchAsync(schedule.BranchKey, schedule.InstanceKey, LaunchSpec.FromSchedule(schedule), ct);
            if (result.JobId is not { } jobId)
                return Results.Problem(
                    result.Detail ?? "Nothing to compare: the instance's old/new folders are empty or unreachable.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            await schedules.TouchLastRunAsync(schedule.Id, DateTimeOffset.UtcNow, ct);
            return Results.Accepted($"/api/v1/jobs/{jobId}", new { jobId });
        }).WithSummary("Run the schedule now (202 + job id; 422 when there is nothing to compare)")
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static bool TryValidateCron(string cron, out string? error)
    {
        try
        {
            CronExpression.Parse(cron);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"Invalid cron expression '{cron}': {ex.Message}";
            return false;
        }
    }

    private static async Task<(Branch? Branch, ComparisonInstance? Instance)> ResolveAsync(
        string branchKey, string instanceKey, IBranchStore branches, IInstanceStore instances, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return (null, null);
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        return (branch, instance);
    }
}
