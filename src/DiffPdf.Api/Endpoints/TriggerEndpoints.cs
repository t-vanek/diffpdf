using DiffPdf.Messaging.Scheduling;
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
            string branchKey, string instanceKey, IBatchLauncher launcher, CancellationToken ct) =>
        {
            var result = await launcher.LaunchAsync(branchKey, instanceKey, LaunchSpec.Default, ct);
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
            string branchKey, IBranchStore branches, IInstanceStore instances, IBatchLauncher launcher, CancellationToken ct) =>
        {
            var branch = await branches.GetByKeyAsync(branchKey, ct);
            if (branch is null)
                return Results.Problem($"Branch '{branchKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            var list = await instances.ListAsync(branch.Id, ct);
            var results = new List<InstanceRunResult>();
            foreach (var instance in list.Where(i => i.Enabled))
            {
                var r = await launcher.LaunchAsync(branchKey, instance.Key, LaunchSpec.Default, ct);
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
    }
}
