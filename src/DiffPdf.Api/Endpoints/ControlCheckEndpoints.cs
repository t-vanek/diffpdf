using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed control checks (top-level, key-addressed): the unified control/monitoring mechanism.
/// The control-plane runner executes the enabled ones on their cadence; this surface is CRUD plus
/// run-now and run history.
/// </summary>
public static class ControlCheckEndpoints
{
    public static void MapControlCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/checks").WithTags("Control checks");

        group.MapPost("/", async (CreateCheckRequest request, IControlCheckStore store, CancellationToken ct) =>
        {
            if (!Validate(request.Key, request.ScopeKind, request.BranchKey, request.InstanceKey, request.Cron, request.IntervalSeconds, out string? error))
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            var check = new ControlCheck
            {
                Id = Guid.NewGuid(),
                Key = request.Key,
                Name = string.IsNullOrWhiteSpace(request.Name) ? request.Key : request.Name,
                Type = request.Type,
                ScopeKind = request.ScopeKind,
                BranchKey = request.BranchKey,
                InstanceKey = request.InstanceKey,
                Cron = request.Cron,
                IntervalSeconds = request.IntervalSeconds,
                Parameters = request.Parameters ?? new Dictionary<string, string>(),
                Events = request.Events ?? [],
                Enabled = request.Enabled,
            };
            try
            {
                var created = await store.CreateAsync(check, ct);
                return Results.Created($"/api/v1/checks/{created.Id}", CheckResponse.From(created));
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Create a control check")
          .Produces<CheckResponse>(StatusCodes.Status201Created)
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (IControlCheckStore store, CancellationToken ct) =>
            Results.Ok((await store.ListAsync(ct)).Select(CheckResponse.From)))
            .WithSummary("List control checks").Produces<IEnumerable<CheckResponse>>();

        group.MapGet("/{id:guid}", async (Guid id, IControlCheckStore store, CancellationToken ct) =>
        {
            var check = await store.GetAsync(id, ct);
            return check is null ? Results.NotFound() : Results.Ok(CheckResponse.From(check));
        }).WithSummary("Get a control check").Produces<CheckResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", async (Guid id, UpdateCheckRequest request, IControlCheckStore store, CancellationToken ct) =>
        {
            if (!Validate(request.Key, request.ScopeKind, request.BranchKey, request.InstanceKey, request.Cron, request.IntervalSeconds, out string? error))
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            var existing = await store.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var updated = existing with
            {
                Key = request.Key,
                Name = string.IsNullOrWhiteSpace(request.Name) ? request.Key : request.Name,
                Type = request.Type,
                ScopeKind = request.ScopeKind,
                BranchKey = request.BranchKey,
                InstanceKey = request.InstanceKey,
                Cron = request.Cron,
                IntervalSeconds = request.IntervalSeconds,
                Parameters = request.Parameters ?? new Dictionary<string, string>(),
                Events = request.Events ?? [],
                Enabled = request.Enabled,
            };
            try
            {
                var saved = await store.UpdateAsync(updated, request.Version, ct);
                return Results.Ok(CheckResponse.From(saved));
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update a control check (optimistic concurrency via Version)")
          .Produces<CheckResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (Guid id, IControlCheckStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithSummary("Delete a control check").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/run", async (Guid id, IControlCheckStore store, IControlCheckRunner runner, CancellationToken ct) =>
        {
            var check = await store.GetAsync(id, ct);
            if (check is null) return Results.NotFound();
            var run = await runner.RunAsync(check, ct);
            return Results.Ok(CheckRunResponse.From(run));
        }).WithSummary("Run a control check now").Produces<CheckRunResponse>().ProducesProblem(StatusCodes.Status404NotFound)
          .RequireRateLimiting("expensive");

        group.MapGet("/{id:guid}/runs", async (Guid id, int? limit, IControlCheckStore store, IControlCheckRunStore runs, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is null) return Results.NotFound();
            var history = await runs.ListByCheckAsync(id, limit is > 0 ? limit.Value : 50, ct);
            return Results.Ok(history.Select(CheckRunResponse.From));
        }).WithSummary("List a control check's run history (newest first)")
          .Produces<IEnumerable<CheckRunResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static bool Validate(
        string key, CheckScopeKind scope, string? branchKey, string? instanceKey,
        string? cron, int? intervalSeconds, out string? error)
    {
        if (!StorageKeyValidator.IsValidKey(key))
        {
            error = "Key must be 1-64 chars of [a-zA-Z0-9_.-] with no '..'.";
            return false;
        }
        if (scope is CheckScopeKind.Branch or CheckScopeKind.Instance && string.IsNullOrWhiteSpace(branchKey))
        {
            error = "BranchKey is required for Branch / Instance scope.";
            return false;
        }
        if (scope is CheckScopeKind.Instance && string.IsNullOrWhiteSpace(instanceKey))
        {
            error = "InstanceKey is required for Instance scope.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cron) && !(intervalSeconds is > 0))
        {
            error = "Either a cron expression or a positive intervalSeconds is required.";
            return false;
        }
        error = null;
        return true;
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
