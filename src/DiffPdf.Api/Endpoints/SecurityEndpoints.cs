using System.Security.Claims;
using DiffPdf.Api.Auth;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Security;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Security surface: the current principal (<c>/security/me</c>), the role legend, the security audit, and
/// admin-only API-key management. Key management requires the Admin role; me/roles need only an authenticated
/// (≥ Viewer) caller. When auth is disabled every route is open (permissive policies).
/// </summary>
public static class SecurityEndpoints
{
    public static void MapSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        var sec = app.MapGroup("/security").WithTags("Security");

        sec.MapGet("/me", (ClaimsPrincipal user) =>
        {
            var roles = user.FindAll(RoleClaims.ClaimType).Select(c => c.Value).Distinct().ToList();
            return Results.Ok(new MeResponse(
                user.ActorOrSystem(), user.AuthMethod(), user.HighestRole(), roles,
                user.Identity?.IsAuthenticated == true));
        }).WithSummary("The current principal: actor, auth method and effective roles").Produces<MeResponse>();

        sec.MapGet("/roles", () => Results.Ok(new[]
        {
            new RoleInfo("Viewer", "Čtení: přehledy, detaily, statistiky a živé aktualizace."),
            new RoleInfo("Operator", "Viewer + zápis: zakládání/úpravy/mazání a spouštění porovnání a spouštěčů."),
            new RoleInfo("Admin", "Operator + správa zabezpečení (API klíče, bezpečnostní audit)."),
        })).WithSummary("Role legend (what each role grants)").Produces<IEnumerable<RoleInfo>>();

        sec.MapGet("/audit", async (IAuditLogStore audit, int? limit, CancellationToken ct) =>
            Results.Ok((await audit.ListAsync("ApiKey", null, limit is > 0 ? limit.Value : 100, ct)).Select(AuditEntryResponse.From)))
            .RequireAdmin().WithSummary("Security audit (API-key lifecycle)").Produces<IEnumerable<AuditEntryResponse>>();

        // All API-key management is Admin-only.
        var keys = app.MapGroup("/apikeys").WithTags("Security").RequireAdmin();

        keys.MapGet("/", async (bool? includeDeleted, IApiKeyService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(includeDeleted ?? false, ct)).Select(ApiKeyResponse.From)))
            .WithSummary("List API keys").Produces<IEnumerable<ApiKeyResponse>>();

        keys.MapGet("/{id:guid}", async (Guid id, IApiKeyService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } k ? Results.Ok(ApiKeyResponse.From(k)) : Results.NotFound())
            .WithSummary("Get an API key").Produces<ApiKeyResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        keys.MapPost("/", async (CreateApiKeyRequest req, IApiKeyService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.Problem("Name is required.", statusCode: StatusCodes.Status400BadRequest);
            var created = await svc.CreateAsync(req.Name, req.Role, req.ExpiresAt, user.Actor(), ct);
            return Results.Created($"/api/v1/apikeys/{created.Key.Id}",
                new CreatedApiKeyResponse(ApiKeyResponse.From(created.Key), created.Plaintext));
        }).WithSummary("Create an API key (returns the plaintext key once)")
          .Produces<CreatedApiKeyResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

        keys.MapPatch("/{id:guid}", async (Guid id, UpdateApiKeyRequest req, IApiKeyService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var k = await svc.UpdateAsync(id, req.Name, req.Role, req.Enabled, req.ExpiresAt, req.Version, user.Actor(), ct);
                return k is null ? Results.NotFound() : Results.Ok(ApiKeyResponse.From(k));
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Update an API key (role/enabled/expiry; optimistic concurrency via Version)")
          .Produces<ApiKeyResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        keys.MapPost("/{id:guid}/revoke", async (Guid id, IApiKeyService svc, ClaimsPrincipal user, CancellationToken ct) =>
            await svc.RevokeAsync(id, user.Actor(), ct) is { } k ? Results.Ok(ApiKeyResponse.From(k)) : Results.NotFound())
            .WithSummary("Revoke an API key (cannot authenticate thereafter; history preserved)")
            .Produces<ApiKeyResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        keys.MapDelete("/{id:guid}", async (Guid id, IApiKeyService svc, ClaimsPrincipal user, CancellationToken ct) =>
            await svc.DeleteAsync(id, user.Actor(), ct) is null ? Results.NotFound() : Results.NoContent())
            .WithSummary("Soft-delete an API key").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
    }
}
