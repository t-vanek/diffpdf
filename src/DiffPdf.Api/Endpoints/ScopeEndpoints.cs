using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Business-instance and project management endpoints.</summary>
public static class ScopeEndpoints
{
    public static void MapScopeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/business-instances").WithTags("Scope");

        group.MapPost("/", async (
            CreateBusinessInstanceRequest request, IBusinessInstanceStore store, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid business instance key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var created = await store.CreateAsync(request.Key, request.Name, ct);
                return Results.Created($"/api/v1/business-instances/{created.Key}", created);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Create a business instance").Produces<BusinessInstance>(StatusCodes.Status201Created)
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (IBusinessInstanceStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)))
            .WithSummary("List business instances").Produces<IReadOnlyList<BusinessInstance>>();

        group.MapGet("/{businessInstanceKey}", async (
            string businessInstanceKey, IBusinessInstanceStore store, CancellationToken ct) =>
        {
            var instance = await store.GetByKeyAsync(businessInstanceKey, ct);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        }).WithSummary("Get a business instance").Produces<BusinessInstance>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{businessInstanceKey}/projects", async (
            string businessInstanceKey, CreateProjectRequest request,
            IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.Problem($"Invalid project key '{request.Key}'.", statusCode: StatusCodes.Status400BadRequest);

            var instance = await instances.GetByKeyAsync(businessInstanceKey, ct);
            if (instance is null)
                return Results.Problem($"Business instance '{businessInstanceKey}' not found.", statusCode: StatusCodes.Status404NotFound);

            try
            {
                var created = await projects.CreateAsync(instance.Id, request.Key, request.Name, ct);
                return Results.Created($"/api/v1/business-instances/{businessInstanceKey}/projects/{created.Key}", created);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithSummary("Create a project under a business instance").Produces<ComparisonProject>(StatusCodes.Status201Created)
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{businessInstanceKey}/projects", async (
            string businessInstanceKey, IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct) =>
        {
            var instance = await instances.GetByKeyAsync(businessInstanceKey, ct);
            if (instance is null) return Results.NotFound();
            return Results.Ok(await projects.ListAsync(instance.Id, ct));
        }).WithSummary("List projects").Produces<IReadOnlyList<ComparisonProject>>().ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{businessInstanceKey}/projects/{projectKey}", async (
            string businessInstanceKey, string projectKey,
            IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct) =>
        {
            var instance = await instances.GetByKeyAsync(businessInstanceKey, ct);
            if (instance is null) return Results.NotFound();
            var project = await projects.GetByKeyAsync(instance.Id, projectKey, ct);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithSummary("Get a project").Produces<ComparisonProject>().ProducesProblem(StatusCodes.Status404NotFound);
    }
}
