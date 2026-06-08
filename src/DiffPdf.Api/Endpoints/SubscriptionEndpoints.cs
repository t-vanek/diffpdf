using DiffPdf.Application.Subscriptions;
using DiffPdf.Core.Storage;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed notification subscriptions (top-level, Id-addressed). The dispatcher reads the enabled
/// ones when a batch finishes; the SMTP transport / base URL stay in appsettings. The handlers bind the
/// request and delegate to <see cref="ISubscriptionService"/> (validation + channel normalization live there).
/// </summary>
public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/subscriptions").WithTags("Subscriptions");

        group.MapPost("/", (CreateSubscriptionRequest request, ISubscriptionService subs, CancellationToken ct) =>
            Run(async () =>
            {
                var created = await subs.CreateAsync(
                    new SubscriptionInput(request.Channel, request.Target, request.Events, request.BranchKey, request.InstanceKey, request.Enabled), ct);
                return Results.Created($"/api/v1/subscriptions/{created.Id}", SubscriptionResponse.From(created));
            }))
            .WithSummary("Create a notification subscription")
            .Produces<SubscriptionResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/", async (ISubscriptionService subs, CancellationToken ct) =>
            Results.Ok((await subs.ListAsync(ct)).Select(SubscriptionResponse.From)))
            .WithSummary("List notification subscriptions").Produces<IEnumerable<SubscriptionResponse>>();

        group.MapGet("/{id:guid}", async (Guid id, ISubscriptionService subs, CancellationToken ct) =>
            await subs.GetAsync(id, ct) is { } sub ? Results.Ok(SubscriptionResponse.From(sub)) : Results.NotFound())
            .WithSummary("Get a subscription").Produces<SubscriptionResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", (Guid id, UpdateSubscriptionRequest request, ISubscriptionService subs, CancellationToken ct) =>
            Run(async () =>
            {
                var saved = await subs.UpdateAsync(
                    id, new SubscriptionInput(request.Channel, request.Target, request.Events, request.BranchKey, request.InstanceKey, request.Enabled), request.Version, ct);
                return saved is null ? Results.NotFound() : Results.Ok(SubscriptionResponse.From(saved));
            }))
            .WithSummary("Update a subscription (optimistic concurrency via Version)")
            .Produces<SubscriptionResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (Guid id, ISubscriptionService subs, CancellationToken ct) =>
            await subs.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithSummary("Delete a subscription").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps the service's validation/conflict outcomes to HTTP (preserving the prior status codes).</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SubscriptionValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}
