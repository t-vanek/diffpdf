using DiffPdf.Application.Automations;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime-managed automations (top-level, key-addressed): the automation engine's definitions —
/// triggers (cron/interval, domain events, manual) plus an ordered step pipeline with timeout/retry/
/// escalation policy. The engine executes the enabled ones; this surface is CRUD plus run-now and run
/// history. The handlers bind the request and delegate to <see cref="IAutomationService"/>.
/// </summary>
public static class AutomationEndpoints
{
    public static void MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/automations").WithTags("Automations");

        group.MapPost("/", (CreateAutomationRequest request, IAutomationService automations, CancellationToken ct) =>
            Run(async () =>
            {
                var created = await automations.CreateAsync(ToInput(request), ct);
                return Results.Created($"/api/v1/automations/{created.Id}", AutomationResponse.From(created));
            }))
            .WithSummary("Create an automation")
            .Produces<AutomationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (AutomationCategory? category, IAutomationService automations, CancellationToken ct) =>
            {
                var all = (await automations.ListAsync(ct)).Select(AutomationResponse.From);
                if (category is { } c) all = all.Where(a => a.Category == c);
                return Results.Ok(all);
            })
            .WithSummary("List automations (optional ?category= filter)").Produces<IEnumerable<AutomationResponse>>();

        group.MapGet("/templates", (AutomationCategory? category) =>
            {
                var templates = AutomationCatalog.Templates.AsEnumerable();
                if (category is { } c) templates = templates.Where(t => t.Category == c);
                return Results.Ok(templates.Select(AutomationTemplateDto.From));
            })
            .WithSummary("List ready-to-edit automation templates, grouped by category (optional ?category= filter)")
            .Produces<IEnumerable<AutomationTemplateDto>>();

        group.MapGet("/catalog", () =>
                Results.Ok(Enum.GetValues<AutomationStepType>().Select(AutomationStepCatalogDto.From)))
            .WithSummary("Step catalog: each step type's category, display name, purpose and parameter schema")
            .Produces<IEnumerable<AutomationStepCatalogDto>>();

        group.MapGet("/{id:guid}", async (Guid id, IAutomationService automations, CancellationToken ct) =>
            await automations.GetAsync(id, ct) is { } automation ? Results.Ok(AutomationResponse.From(automation)) : Results.NotFound())
            .WithSummary("Get an automation").Produces<AutomationResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", (Guid id, UpdateAutomationRequest request, IAutomationService automations, CancellationToken ct) =>
            Run(async () =>
            {
                var saved = await automations.UpdateAsync(id, ToInput(request), request.Version, ct);
                return saved is null ? Results.NotFound() : Results.Ok(AutomationResponse.From(saved));
            }))
            .WithSummary("Update an automation (optimistic concurrency via Version)")
            .Produces<AutomationResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (Guid id, IAutomationService automations, CancellationToken ct) =>
            await automations.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithSummary("Delete an automation").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        // Quick mute toggle — no Version handshake, so it never conflicts with an open editor.
        group.MapPost("/{id:guid}/notifications/enable", async (Guid id, IAutomationStore store, CancellationToken ct) =>
            await store.SetNotificationsEnabledAsync(id, true, ct) is { } a
                ? Results.Ok(AutomationResponse.From(a)) : Results.NotFound())
            .WithSummary("Unmute the automation's outbound notifications")
            .Produces<AutomationResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/notifications/disable", async (Guid id, IAutomationStore store, CancellationToken ct) =>
            await store.SetNotificationsEnabledAsync(id, false, ct) is { } a
                ? Results.Ok(AutomationResponse.From(a)) : Results.NotFound())
            .WithSummary("Mute the automation's outbound notifications (it keeps running; runs stay in history)")
            .Produces<AutomationResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/run", (Guid id, IAutomationService automations, CancellationToken ct) =>
            Run(async () =>
                await automations.RunNowAsync(id, ct) is { } run ? Results.Ok(AutomationRunResponse.From(run)) : Results.NotFound()))
            .WithSummary("Run an automation now (409 when a run is already in flight)")
            .Produces<AutomationRunResponse>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting("expensive");

        group.MapGet("/{id:guid}/runs", async (Guid id, int? limit, IAutomationService automations, CancellationToken ct) =>
            await automations.ListRunsAsync(id, limit is > 0 ? limit.Value : 50, ct) is { } history
                ? Results.Ok(history.Select(AutomationRunResponse.From))
                : Results.NotFound())
            .WithSummary("List an automation's run history (newest first, one row per attempt)")
            .Produces<IEnumerable<AutomationRunResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static AutomationInput ToInput(CreateAutomationRequest r) => new(
        r.Key, r.Name, r.ScopeKind, r.BranchKey, r.InstanceKey, r.Cron, r.IntervalSeconds,
        r.EventTriggers, r.EventDebounceSeconds, r.Steps?.Select(s => s.ToStep()).ToList(),
        r.TimeoutSeconds, r.MaxAttempts, r.RetryDelaySeconds, r.FailureThreshold, r.Events, r.Enabled,
        r.NotificationsEnabled);

    private static AutomationInput ToInput(UpdateAutomationRequest r) => new(
        r.Key, r.Name, r.ScopeKind, r.BranchKey, r.InstanceKey, r.Cron, r.IntervalSeconds,
        r.EventTriggers, r.EventDebounceSeconds, r.Steps?.Select(s => s.ToStep()).ToList(),
        r.TimeoutSeconds, r.MaxAttempts, r.RetryDelaySeconds, r.FailureThreshold, r.Events, r.Enabled,
        r.NotificationsEnabled);

    /// <summary>Maps the service's validation/conflict outcomes to HTTP.</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AutomationValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (AutomationBusyException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (DuplicateKeyException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}

/// <summary>One parameter a step accepts, as documented by the catalog (drives a typed UI field).</summary>
public sealed record AutomationParameterSpecDto(
    string Key,
    string Label,
    string Help,
    AutomationParameterType Type,
    string? Default,
    int? Min,
    int? Max,
    IReadOnlyList<string>? EnumValues)
{
    public static AutomationParameterSpecDto From(AutomationParameterSpec p) =>
        new(p.Key, p.Label, p.Help, p.Type, p.Default, p.Min, p.Max, p.EnumValues);
}

/// <summary>Catalog entry for one step type: its category, human metadata and parameter schema.</summary>
public sealed record AutomationStepCatalogDto(
    AutomationStepType Type,
    AutomationCategory Category,
    string DisplayName,
    string Purpose,
    IReadOnlyList<AutomationParameterSpecDto> Parameters)
{
    public static AutomationStepCatalogDto From(AutomationStepType type) => new(
        type, AutomationCatalog.CategoryOf(type), AutomationCatalog.DisplayNameFor(type),
        AutomationCatalog.PurposeFor(type),
        AutomationCatalog.ParametersFor(type).Select(AutomationParameterSpecDto.From).ToList());
}

/// <summary>A ready-to-edit automation template as returned by the API.</summary>
public sealed record AutomationTemplateDto(
    string Key,
    AutomationCategory Category,
    string DisplayName,
    string Purpose,
    string Icon,
    AutomationScopeKind DefaultScope,
    IReadOnlyList<AutomationStepDto> Steps,
    string? RecommendedCron,
    int? RecommendedIntervalSeconds,
    IReadOnlyList<NotificationEvent> DefaultEvents)
{
    public static AutomationTemplateDto From(AutomationTemplate t) => new(
        t.Key, t.Category, t.DisplayName, t.Purpose, t.Icon, t.DefaultScope,
        t.Steps.Select(AutomationStepDto.From).ToList(),
        t.RecommendedCron, t.RecommendedIntervalSeconds, t.DefaultEvents ?? []);
}

/// <summary>One step of an automation's pipeline as accepted/returned by the API.</summary>
public sealed record AutomationStepDto(
    AutomationStepType Type,
    string? Name = null,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    public AutomationStep ToStep() => new() { Type = Type, Name = Name, Parameters = Parameters ?? new Dictionary<string, string>() };
    public static AutomationStepDto From(AutomationStep s) => new(s.Type, s.Name, s.Parameters);
}

/// <summary>Create an automation.</summary>
public sealed record CreateAutomationRequest(
    string Key,
    string Name,
    IReadOnlyList<AutomationStepDto> Steps,
    AutomationScopeKind ScopeKind = AutomationScopeKind.Global,
    string? BranchKey = null,
    string? InstanceKey = null,
    string? Cron = null,
    int? IntervalSeconds = null,
    IReadOnlyList<NotificationEvent>? EventTriggers = null,
    int EventDebounceSeconds = 60,
    int TimeoutSeconds = 600,
    int MaxAttempts = 1,
    int RetryDelaySeconds = 30,
    int FailureThreshold = 3,
    IReadOnlyList<NotificationEvent>? Events = null,
    bool Enabled = true,
    bool NotificationsEnabled = true);

/// <summary>Update an automation (optimistic concurrency via <see cref="Version"/>).</summary>
public sealed record UpdateAutomationRequest(
    string Key,
    string Name,
    IReadOnlyList<AutomationStepDto> Steps,
    long Version,
    AutomationScopeKind ScopeKind = AutomationScopeKind.Global,
    string? BranchKey = null,
    string? InstanceKey = null,
    string? Cron = null,
    int? IntervalSeconds = null,
    IReadOnlyList<NotificationEvent>? EventTriggers = null,
    int EventDebounceSeconds = 60,
    int TimeoutSeconds = 600,
    int MaxAttempts = 1,
    int RetryDelaySeconds = 30,
    int FailureThreshold = 3,
    IReadOnlyList<NotificationEvent>? Events = null,
    bool Enabled = true,
    bool NotificationsEnabled = true);

/// <summary>An automation as returned by the API. <c>NextRunAt</c> / <c>RunningSince</c> /
/// <c>ConsecutiveFailures</c> are engine state — read-only.</summary>
public sealed record AutomationResponse(
    Guid Id,
    string Key,
    string Name,
    AutomationScopeKind ScopeKind,
    string? BranchKey,
    string? InstanceKey,
    string? Cron,
    int? IntervalSeconds,
    IReadOnlyList<NotificationEvent> EventTriggers,
    int EventDebounceSeconds,
    IReadOnlyList<AutomationStepDto> Steps,
    int TimeoutSeconds,
    int MaxAttempts,
    int RetryDelaySeconds,
    int FailureThreshold,
    IReadOnlyList<NotificationEvent> Events,
    bool Enabled,
    bool NotificationsEnabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? RunningSince,
    int ConsecutiveFailures,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastRunAt,
    AutomationRunOutcome? LastOutcome,
    long Version,
    AutomationCategory Category,
    string Purpose)
{
    public static AutomationResponse From(Automation a) => new(
        a.Id, a.Key, a.Name, a.ScopeKind, a.BranchKey, a.InstanceKey, a.Cron, a.IntervalSeconds,
        a.EventTriggers, a.EventDebounceSeconds, a.Steps.Select(AutomationStepDto.From).ToList(),
        a.TimeoutSeconds, a.MaxAttempts, a.RetryDelaySeconds, a.FailureThreshold, a.Events, a.Enabled,
        a.NotificationsEnabled,
        a.NextRunAt, a.RunningSince, a.ConsecutiveFailures,
        a.CreatedAt, a.UpdatedAt, a.LastRunAt, a.LastOutcome, a.Version,
        AutomationCatalog.CategoryOf(a), AutomationCatalog.PurposeFor(a));
}

/// <summary>One step result within an automation run as returned by the API.</summary>
public sealed record AutomationStepResultDto(
    int Index,
    AutomationStepType Type,
    string? Name,
    AutomationRunOutcome Outcome,
    string Detail,
    double DurationSeconds)
{
    public static AutomationStepResultDto From(AutomationStepResult r) =>
        new(r.Index, r.Type, r.Name, r.Outcome, r.Detail, r.DurationSeconds);
}

/// <summary>One automation run (attempt) as returned by the API.</summary>
public sealed record AutomationRunResponse(
    Guid Id,
    Guid AutomationId,
    AutomationTriggerKind Trigger,
    string? TriggerDetail,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    AutomationRunOutcome Outcome,
    string? Detail,
    IReadOnlyList<AutomationStepResultDto> StepResults)
{
    public static AutomationRunResponse From(AutomationRun r) => new(
        r.Id, r.AutomationId, r.Trigger, r.TriggerDetail, r.Attempt, r.StartedAt, r.CompletedAt,
        r.Outcome, r.Detail, r.StepResults.Select(AutomationStepResultDto.From).ToList());
}
