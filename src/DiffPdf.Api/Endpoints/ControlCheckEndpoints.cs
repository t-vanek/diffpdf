using DiffPdf.Application.ControlChecks;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed control checks (top-level, key-addressed): the unified control/monitoring mechanism.
/// The control-plane runner executes the enabled ones on their cadence; this surface is CRUD plus run-now
/// and run history. The handlers bind the request and delegate to <see cref="IControlCheckService"/>.
/// </summary>
public static class ControlCheckEndpoints
{
    public static void MapControlCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/checks").WithTags("Control checks");

        group.MapPost("/", (CreateCheckRequest request, IControlCheckService checks, CancellationToken ct) =>
            Run(async () =>
            {
                var created = await checks.CreateAsync(ToInput(request), ct);
                return Results.Created($"/api/v1/checks/{created.Id}", CheckResponse.From(created));
            }))
            .WithSummary("Create a control check")
            .Produces<CheckResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (IControlCheckService checks, CancellationToken ct) =>
            Results.Ok((await checks.ListAsync(ct)).Select(CheckResponse.From)))
            .WithSummary("List control checks").Produces<IEnumerable<CheckResponse>>();

        group.MapGet("/{id:guid}", async (Guid id, IControlCheckService checks, CancellationToken ct) =>
            await checks.GetAsync(id, ct) is { } check ? Results.Ok(CheckResponse.From(check)) : Results.NotFound())
            .WithSummary("Get a control check").Produces<CheckResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", (Guid id, UpdateCheckRequest request, IControlCheckService checks, CancellationToken ct) =>
            Run(async () =>
            {
                var saved = await checks.UpdateAsync(id, ToInput(request), request.Version, ct);
                return saved is null ? Results.NotFound() : Results.Ok(CheckResponse.From(saved));
            }))
            .WithSummary("Update a control check (optimistic concurrency via Version)")
            .Produces<CheckResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (Guid id, IControlCheckService checks, CancellationToken ct) =>
            await checks.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithSummary("Delete a control check").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/run", async (Guid id, IControlCheckService checks, CancellationToken ct) =>
            await checks.RunAsync(id, ct) is { } run ? Results.Ok(CheckRunResponse.From(run)) : Results.NotFound())
            .WithSummary("Run a control check now").Produces<CheckRunResponse>().ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("expensive");

        group.MapGet("/{id:guid}/runs", async (Guid id, int? limit, IControlCheckService checks, CancellationToken ct) =>
            await checks.ListRunsAsync(id, limit is > 0 ? limit.Value : 50, ct) is { } history
                ? Results.Ok(history.Select(CheckRunResponse.From))
                : Results.NotFound())
            .WithSummary("List a control check's run history (newest first)")
            .Produces<IEnumerable<CheckRunResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static CheckInput ToInput(CreateCheckRequest r) => new(
        r.Key, r.Name, r.Type, r.ScopeKind, r.BranchKey, r.InstanceKey, r.Cron, r.IntervalSeconds, r.Parameters, r.Events, r.Enabled);

    private static CheckInput ToInput(UpdateCheckRequest r) => new(
        r.Key, r.Name, r.Type, r.ScopeKind, r.BranchKey, r.InstanceKey, r.Cron, r.IntervalSeconds, r.Parameters, r.Events, r.Enabled);

    /// <summary>Maps the service's validation/conflict outcomes to HTTP (preserving the prior status codes).</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CheckValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (DuplicateKeyException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}

/// <summary>Create a control check.</summary>
public sealed record CreateCheckRequest(
    string Key,
    string Name,
    CheckType Type,
    CheckScopeKind ScopeKind = CheckScopeKind.Global,
    string? BranchKey = null,
    string? InstanceKey = null,
    string? Cron = null,
    int? IntervalSeconds = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    IReadOnlyList<NotificationEvent>? Events = null,
    bool Enabled = true);

/// <summary>Update a control check (optimistic concurrency via <see cref="Version"/>).</summary>
public sealed record UpdateCheckRequest(
    string Key,
    string Name,
    CheckType Type,
    long Version,
    CheckScopeKind ScopeKind = CheckScopeKind.Global,
    string? BranchKey = null,
    string? InstanceKey = null,
    string? Cron = null,
    int? IntervalSeconds = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    IReadOnlyList<NotificationEvent>? Events = null,
    bool Enabled = true);

/// <summary>A control check as returned by the API.</summary>
public sealed record CheckResponse(
    Guid Id,
    string Key,
    string Name,
    CheckType Type,
    CheckScopeKind ScopeKind,
    string? BranchKey,
    string? InstanceKey,
    string? Cron,
    int? IntervalSeconds,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<NotificationEvent> Events,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastRunAt,
    CheckRunOutcome? LastOutcome,
    long Version)
{
    public static CheckResponse From(ControlCheck c) => new(
        c.Id, c.Key, c.Name, c.Type, c.ScopeKind, c.BranchKey, c.InstanceKey, c.Cron, c.IntervalSeconds,
        c.Parameters, c.Events, c.Enabled, c.CreatedAt, c.UpdatedAt, c.LastRunAt, c.LastOutcome, c.Version);
}

/// <summary>One control-check run as returned by the API.</summary>
public sealed record CheckRunResponse(
    Guid Id,
    Guid CheckId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    CheckRunOutcome Outcome,
    string? Detail)
{
    public static CheckRunResponse From(ControlCheckRun r) => new(
        r.Id, r.CheckId, r.StartedAt, r.CompletedAt, r.Outcome, r.Detail);
}
