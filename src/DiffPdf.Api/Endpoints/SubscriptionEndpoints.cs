using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed notification subscriptions (top-level, Id-addressed). The dispatcher reads
/// the enabled ones when a batch finishes; the SMTP transport / base URL stay in appsettings.
/// </summary>
public static class SubscriptionEndpoints
{
    private static readonly string[] ValidChannels = ["webhook", "smtp"];

    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/subscriptions").WithTags("Subscriptions");

        group.MapPost("/", async (CreateSubscriptionRequest request, ISubscriptionStore store, CancellationToken ct) =>
        {
            if (!Validate(request.Channel, request.Target, request.Events, out string? error))
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            var sub = new NotificationSubscription
            {
                Id = Guid.NewGuid(),
                Channel = request.Channel.ToLowerInvariant(),
                Target = request.Target,
                Events = request.Events,
                BranchKey = request.BranchKey,
                InstanceKey = request.InstanceKey,
                Enabled = request.Enabled,
            };
            var created = await store.CreateAsync(sub, ct);
            return Results.Created($"/api/v1/subscriptions/{created.Id}", SubscriptionResponse.From(created));
        }).WithSummary("Create a notification subscription")
          .Produces<SubscriptionResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/", async (ISubscriptionStore store, CancellationToken ct) =>
            Results.Ok((await store.ListAsync(ct)).Select(SubscriptionResponse.From)))
            .WithSummary("List notification subscriptions").Produces<IEnumerable<SubscriptionResponse>>();

        group.MapGet("/{id:guid}", async (Guid id, ISubscriptionStore store, CancellationToken ct) =>
        {
            var sub = await store.GetAsync(id, ct);
            return sub is null ? Results.NotFound() : Results.Ok(SubscriptionResponse.From(sub));
        }).WithSummary("Get a subscription").Produces<SubscriptionResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", async (Guid id, UpdateSubscriptionRequest request, ISubscriptionStore store, CancellationToken ct) =>
        {
            if (!Validate(request.Channel, request.Target, request.Events, out string? error))
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            var existing = await store.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var updated = existing with
            {
                Channel = request.Channel.ToLowerInvariant(),
                Target = request.Target,
                Events = request.Events,
                BranchKey = request.BranchKey,
                InstanceKey = request.InstanceKey,
                Enabled = request.Enabled,
            };
            try
            {
                var saved = await store.UpdateAsync(updated, request.Version, ct);
                return Results.Ok(SubscriptionResponse.From(saved));
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update a subscription (optimistic concurrency via Version)")
          .Produces<SubscriptionResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (Guid id, ISubscriptionStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithSummary("Delete a subscription").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static bool Validate(string channel, string target, IReadOnlyList<NotificationEvent> events, out string? error)
    {
        if (string.IsNullOrWhiteSpace(channel) || !ValidChannels.Contains(channel.ToLowerInvariant()))
        {
            error = $"Channel must be one of: {string.Join(", ", ValidChannels)}.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "Target must not be empty.";
            return false;
        }
        if (events is null || events.Count == 0)
        {
            error = "Events must not be empty.";
            return false;
        }
        error = null;
        return true;
    }
}
