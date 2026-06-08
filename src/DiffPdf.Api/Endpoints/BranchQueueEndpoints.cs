using DiffPdf.Application.Queue;
using DiffPdf.Core.Abstractions;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Per-branch sequential run-queue controls. Each instance runs one at a time within its branch; the
/// actions (enqueue / run / pause / resume / stop) drive that queue. GET returns the live per-branch +
/// per-instance state (the desktop also receives pushes on the SignalR "queueState" event).
/// </summary>
public static class BranchQueueEndpoints
{
    public static void MapBranchQueueEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/branches/{branchKey}/queue", async (string branchKey, IBranchQueueControl control, CancellationToken ct) =>
            await control.GetStateAsync(branchKey, ct) is { } state ? Results.Ok(state) : Results.NotFound())
            .WithTags("Queue")
            .WithSummary("Get a branch's run-queue state (per-branch hold + counts + per-instance status).")
            .Produces<BranchQueueState>().ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/branches/{branchKey}/queue", async (
            string branchKey, QueueActionRequest req, IBranchQueueControl control, CancellationToken ct) =>
            await control.ActOnBranchAsync(branchKey, req.Action, ct) is { } outcome ? Results.Ok(outcome) : Results.NotFound())
            .WithTags("Queue")
            .WithSummary("Run-queue action for a whole branch (enqueue/run all enabled instances; pause/resume/stop cascade).")
            .Produces<QueueActionOutcome>().ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/branches/{branchKey}/instances/{instanceKey}/queue", async (
            string branchKey, string instanceKey, QueueActionRequest req, IBranchQueueControl control, CancellationToken ct) =>
            await control.ActOnInstanceAsync(branchKey, instanceKey, req.Action, ct) is { } outcome ? Results.Ok(outcome) : Results.NotFound())
            .WithTags("Queue")
            .WithSummary("Run-queue action for a single instance (enqueue/run/pause/resume/stop).")
            .Produces<QueueActionOutcome>().ProducesProblem(StatusCodes.Status404NotFound);
    }
}
