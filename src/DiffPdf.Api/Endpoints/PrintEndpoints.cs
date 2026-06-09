using DiffPdf.Application.Abstractions;
using DiffPdf.Messaging.Messages;
using Wolverine;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// "Vytisknout a porovnat": print a fresh sample batch on the D3Soft Tisk SOA, fetch it together with the
/// previous batch, and run a normal DiffPdf comparison. The operation is long-running with real side
/// effects, so the POST only enqueues a durable orchestration and returns an operation id the client polls
/// for progress (print → fetch → launch); the resulting comparison then appears in the Jobs list as usual.
/// </summary>
public static class PrintEndpoints
{
    public static void MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        // Which scopes have a print source — lets the client show the action only where it works.
        app.MapGet("/print-sources", (IPrintSourceResolver resolver) =>
                Results.Ok(new PrintSourcesResponse(resolver.ConfiguredScopes.ToArray())))
            .WithTags("Print")
            .WithSummary("List scopes (branchKey/instanceKey) that have a configured print source")
            .Produces<PrintSourcesResponse>();

        // Start a print-and-compare for one instance.
        app.MapPost("/triggers/{branchKey}/{instanceKey}/print", async (
            string branchKey, string instanceKey,
            IPrintSourceResolver resolver, IPrintOperationStore operations, IMessageBus bus, CancellationToken ct) =>
        {
            if (!resolver.IsConfigured(branchKey, instanceKey))
                return Results.Problem(
                    $"Instance '{branchKey}/{instanceKey}' has no configured print source.",
                    statusCode: StatusCodes.Status404NotFound);

            var operationId = Guid.NewGuid();
            operations.Start(operationId, branchKey, instanceKey);
            // Durable enqueue: the orchestration survives a restart; the in-memory status is best-effort.
            await bus.PublishAsync(new PrintAndCompareBatch(operationId, branchKey, instanceKey));

            var body = operations.Get(operationId) is { } s
                ? PrintOperationResponse.From(s)
                : new PrintOperationResponse(operationId, branchKey, instanceKey, nameof(PrintPhase.Printing), null, null, null, null, null);
            return Results.Accepted($"/api/v1/print-operations/{operationId}", body);
        })
        .WithTags("Print")
        .WithSummary("Print a fresh sample batch and compare it to the previous batch (enqueues; returns an operation id)")
        .Produces<PrintOperationResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireRateLimiting("expensive");

        // Poll a print operation's progress (print → fetch → launch → the comparison job id).
        app.MapGet("/print-operations/{operationId:guid}", (Guid operationId, IPrintOperationStore operations) =>
                operations.Get(operationId) is { } s ? Results.Ok(PrintOperationResponse.From(s)) : Results.NotFound())
            .WithTags("Print")
            .WithSummary("Get a print-and-compare operation's status")
            .Produces<PrintOperationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

/// <summary>Configured print-source scope keys ("branchKey/instanceKey").</summary>
public sealed record PrintSourcesResponse(IReadOnlyList<string> Scopes);

/// <summary>Client view of a print-and-compare operation. <see cref="JobId"/> is set once the comparison launches.</summary>
public sealed record PrintOperationResponse(
    Guid OperationId,
    string BranchKey,
    string InstanceKey,
    string Phase,
    string? NewBatch,
    string? PreviousBatch,
    Guid? JobId,
    string? ValidationSummary,
    string? Error)
{
    public static PrintOperationResponse From(PrintOperationStatus s) =>
        new(s.OperationId, s.BranchKey, s.InstanceKey, s.Phase.ToString(),
            s.NewBatch, s.PreviousBatch, s.JobId, s.ValidationSummary, s.Error);
}
