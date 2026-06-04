using Cronos;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Messaging.ControlPlane;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Per-scope configuration of triggers and comparers (global / branch / instance) with inheritance. GET
/// returns both the stored settings and the server-resolved effective configuration, so the desktop client
/// never computes inheritance itself. PUT upserts this level's sources + custom payloads.
/// </summary>
public static class ScopeConfigurationEndpoints
{
    public static void MapScopeConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/config/global", async (IScopeConfigurationService svc, CancellationToken ct) =>
            Results.Ok(ToResponse(await svc.GetGlobalAsync(ct), null, null)))
            .WithTags("Configuration")
            .WithSummary("Get the global trigger + comparer configuration.")
            .Produces<ScopeConfigResponse>();

        app.MapPut("/config/global", async (UpsertScopeConfigRequest req, IScopeConfigurationService svc, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(ToResponse(await svc.UpsertGlobalAsync(ToInput(req), ct), null, null));
            }
            catch (ScopeConfigValidationException ex) { return Validation(ex); }
            catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        })
            .WithTags("Configuration")
            .WithSummary("Update the global trigger + comparer configuration.")
            .Produces<ScopeConfigResponse>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting("expensive");

        app.MapGet("/branches/{branchKey}/config", async (string branchKey, IScopeConfigurationService svc, CancellationToken ct) =>
            await svc.GetBranchAsync(branchKey, ct) is { } v ? Results.Ok(ToResponse(v, branchKey, null)) : Results.NotFound())
            .WithTags("Configuration")
            .WithSummary("Get a branch's trigger + comparer configuration (stored + effective).")
            .Produces<ScopeConfigResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/branches/{branchKey}/config", async (string branchKey, UpsertScopeConfigRequest req, IScopeConfigurationService svc, CancellationToken ct) =>
        {
            try
            {
                var v = await svc.UpsertBranchAsync(branchKey, ToInput(req), ct);
                return v is null ? Results.NotFound() : Results.Ok(ToResponse(v, branchKey, null));
            }
            catch (ScopeConfigValidationException ex) { return Validation(ex); }
            catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        })
            .WithTags("Configuration")
            .WithSummary("Update a branch's trigger + comparer configuration.")
            .Produces<ScopeConfigResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting("expensive");

        app.MapGet("/branches/{branchKey}/instances/{instanceKey}/config", async (
            string branchKey, string instanceKey, IScopeConfigurationService svc, CancellationToken ct) =>
            await svc.GetInstanceAsync(branchKey, instanceKey, ct) is { } v
                ? Results.Ok(ToResponse(v, branchKey, instanceKey))
                : Results.NotFound())
            .WithTags("Configuration")
            .WithSummary("Get an instance's trigger + comparer configuration (stored + effective).")
            .Produces<ScopeConfigResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/branches/{branchKey}/instances/{instanceKey}/config", async (
            string branchKey, string instanceKey, UpsertScopeConfigRequest req, IScopeConfigurationService svc, CancellationToken ct) =>
        {
            try
            {
                var v = await svc.UpsertInstanceAsync(branchKey, instanceKey, ToInput(req), ct);
                return v is null ? Results.NotFound() : Results.Ok(ToResponse(v, branchKey, instanceKey));
            }
            catch (ScopeConfigValidationException ex) { return Validation(ex); }
            catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        })
            .WithTags("Configuration")
            .WithSummary("Update an instance's trigger + comparer configuration.")
            .Produces<ScopeConfigResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting("expensive");

        // ---- Scheduled runs (gear "Konfigurace" → Plán běhu), backed by a ScheduledComparison control check. ----

        app.MapGet("/branches/{branchKey}/schedule", async (string branchKey, IScheduleService svc, CancellationToken ct) =>
            await svc.GetBranchScheduleAsync(branchKey, ct) is { } v ? Results.Ok(new ScheduleResponse(v.Enabled, v.Cron)) : Results.NotFound())
            .WithTags("Configuration").WithSummary("Get a branch's scheduled-run setting.")
            .Produces<ScheduleResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/branches/{branchKey}/schedule", async (string branchKey, SetScheduleRequest req, IScheduleService svc, CancellationToken ct) =>
        {
            if (InvalidCron(req) is { } bad) return bad;
            try
            {
                var v = await svc.SetBranchScheduleAsync(branchKey, req.Enabled, req.Cron ?? string.Empty, ct);
                return v is null ? Results.NotFound() : Results.Ok(new ScheduleResponse(v.Enabled, v.Cron));
            }
            catch (ScheduleValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        })
            .WithTags("Configuration").WithSummary("Enable/disable a branch's scheduled run.")
            .Produces<ScheduleResponse>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("expensive");

        app.MapGet("/branches/{branchKey}/instances/{instanceKey}/schedule", async (
            string branchKey, string instanceKey, IScheduleService svc, CancellationToken ct) =>
            await svc.GetInstanceScheduleAsync(branchKey, instanceKey, ct) is { } v ? Results.Ok(new ScheduleResponse(v.Enabled, v.Cron)) : Results.NotFound())
            .WithTags("Configuration").WithSummary("Get an instance's scheduled-run setting.")
            .Produces<ScheduleResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/branches/{branchKey}/instances/{instanceKey}/schedule", async (
            string branchKey, string instanceKey, SetScheduleRequest req, IScheduleService svc, CancellationToken ct) =>
        {
            if (InvalidCron(req) is { } bad) return bad;
            try
            {
                var v = await svc.SetInstanceScheduleAsync(branchKey, instanceKey, req.Enabled, req.Cron ?? string.Empty, ct);
                return v is null ? Results.NotFound() : Results.Ok(new ScheduleResponse(v.Enabled, v.Cron));
            }
            catch (ScheduleValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        })
            .WithTags("Configuration").WithSummary("Enable/disable an instance's scheduled run.")
            .Produces<ScheduleResponse>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("expensive");
    }

    // A 5-field UTC cron is required when enabling a schedule; reject anything Cronos can't parse.
    private static IResult? InvalidCron(SetScheduleRequest req) =>
        req.Enabled && (string.IsNullOrWhiteSpace(req.Cron) || !CronExpression.TryParse(req.Cron, CronFormat.Standard, out _))
            ? Results.Problem($"Invalid cron '{req.Cron}'. Use a 5-field UTC cron, e.g. '0 6 * * *'.", statusCode: StatusCodes.Status400BadRequest)
            : null;

    private static UpsertScopeConfigInput ToInput(UpsertScopeConfigRequest req) => new()
    {
        TriggerSource = req.TriggerSource,
        TriggerConfig = req.TriggerConfig,
        ComparerSource = req.ComparerSource,
        ComparisonOptions = req.ComparisonOptions,
        ExpectedVersion = req.Version,
    };

    private static ScopeConfigResponse ToResponse(ScopeConfigurationView v, string? branchKey, string? instanceKey) => new(
        v.Stored.Level, branchKey, instanceKey,
        v.Stored.TriggerSource, v.Stored.TriggerConfig,
        v.Stored.ComparerSource, v.Stored.ComparisonOptions,
        v.Effective.TriggerConfig, v.Effective.TriggerResolvedFrom,
        v.Effective.ComparisonOptions, v.Effective.ComparerResolvedFrom,
        v.Stored.UpdatedAt, v.Stored.Version);

    private static IResult Validation(ScopeConfigValidationException ex) =>
        Results.Json(new TriggerErrorResponse(false, ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status400BadRequest);
}
