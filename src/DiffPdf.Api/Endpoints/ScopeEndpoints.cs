using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Api.Endpoints;

/// <summary>Business-instance and project management endpoints.</summary>
public static class ScopeEndpoints
{
    public static void MapScopeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/business-instances", async (
            CreateBusinessInstanceRequest request, IBusinessInstanceStore store, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.BadRequest(new { error = $"Invalid business instance key '{request.Key}'." });
            try
            {
                var created = await store.CreateAsync(request.Key, request.Name, ct);
                return Results.Created($"/api/business-instances/{created.Key}", created);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapGet("/api/business-instances", async (IBusinessInstanceStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        app.MapGet("/api/business-instances/{businessInstanceKey}", async (
            string businessInstanceKey, IBusinessInstanceStore store, CancellationToken ct) =>
        {
            var instance = await store.GetByKeyAsync(businessInstanceKey, ct);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        app.MapPost("/api/business-instances/{businessInstanceKey}/projects", async (
            string businessInstanceKey, CreateProjectRequest request,
            IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct) =>
        {
            if (!StorageKeyValidator.IsValidKey(request.Key))
                return Results.BadRequest(new { error = $"Invalid project key '{request.Key}'." });

            var instance = await instances.GetByKeyAsync(businessInstanceKey, ct);
            if (instance is null)
                return Results.NotFound(new { error = $"Business instance '{businessInstanceKey}' not found." });

            try
            {
                var created = await projects.CreateAsync(instance.Id, request.Key, request.Name, ct);
                return Results.Created($"/api/business-instances/{businessInstanceKey}/projects/{created.Key}", created);
            }
            catch (DuplicateKeyException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapGet("/api/business-instances/{businessInstanceKey}/projects", async (
            string businessInstanceKey, IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct) =>
        {
            var instance = await instances.GetByKeyAsync(businessInstanceKey, ct);
            if (instance is null) return Results.NotFound();
            return Results.Ok(await projects.ListAsync(instance.Id, ct));
        });

        app.MapGet("/api/business-instances/{businessInstanceKey}/projects/{projectKey}", async (
            string businessInstanceKey, string projectKey,
            IBusinessInstanceStore instances, IProjectStore projects, CancellationToken ct) =>
        {
            var instance = await instances.GetByKeyAsync(businessInstanceKey, ct);
            if (instance is null) return Results.NotFound();
            var project = await projects.GetByKeyAsync(instance.Id, projectKey, ct);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });
    }
}
