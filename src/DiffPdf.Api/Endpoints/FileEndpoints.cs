using System.Security.Claims;
using DiffPdf.Application.Files;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// PDF file manager: list / upload / create-folder / delete / rename / download over the configured
/// FileManager root. Handlers bind the request, call <see cref="IFileManagerService"/> and map the
/// outcome to HTTP — all path validation and filesystem work lives in DiffPdf.Application. Paths on
/// the wire are virtual (root-relative); the service guarantees they cannot escape the root.
/// </summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/files").WithTags("Files");

        group.MapGet("/", (string? path, IFileManagerService files) =>
        {
            var result = files.List(path);
            return result.Status is FileOpStatus.Ok
                ? Results.Ok(new FileListResponse
                {
                    CurrentPath = result.CurrentPath,
                    ParentPath = result.ParentPath,
                    Items = result.Items!.Select(FileItemDto.From).ToList(),
                })
                : Problem(result.Status);
        })
        .WithSummary("List a folder (folders + PDF files; path is root-relative, empty = root)")
        .Produces<FileListResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // Multipart upload of one or more PDFs into a target folder. Mirrors /comparisons/preview-upload
        // (DisableAntiforgery: bearer-token API, no cookies). Per-file failures are soft — reported in the
        // body — so one bad file does not fail the batch.
        group.MapPost("/upload", async (
            HttpRequest request,
            IFileManagerService files,
            IOptions<FileManagerOptions> options,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.Problem("Expected multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);

            // Kestrel's default body cap is 30 MB — raise it to the configured upload limit before the body
            // is read (plus headroom for the multipart framing). Null/read-only feature = TestServer; skip.
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = options.Value.MaxUploadSizeBytes + 1024 * 1024;

            var form = await request.ReadFormAsync(ct);
            string? path = form["path"];
            bool overwrite = bool.TryParse(form["overwrite"], out bool o) && o;

            if (form.Files.Count == 0)
                return Results.Problem("At least one file is required.", statusCode: StatusCodes.Status400BadRequest);

            var directory = files.ValidateDirectory(path);
            if (directory.Status is not FileOpStatus.Ok)
                return Problem(directory.Status, directory.Detail);

            var results = new List<UploadFileResponse>(form.Files.Count);
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                results.Add(UploadFileResponse.From(await files.UploadAsync(path, file.FileName, stream, overwrite, ct)));
            }
            return Results.Ok(new UploadFilesResponse { Files = results });
        })
        .DisableAntiforgery()
        .WithSummary("Upload one or more PDFs (multipart/form-data: path, overwrite, files)")
        .Produces<UploadFilesResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/folder", (CreateFolderRequest request, IFileManagerService files) =>
        {
            var result = files.CreateFolder(request.Path, request.FolderName);
            return result.Status is FileOpStatus.Ok
                ? Results.Created($"/api/v1/files?path={Uri.EscapeDataString(result.Entry!.Path)}", FileItemDto.From(result.Entry!))
                : Problem(result.Status, result.Detail);
        })
        .WithSummary("Create a folder")
        .Produces<FileItemDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // DELETE with query parameters (not a body) — matches the branch delete's ?cascade=true convention.
        group.MapDelete("/", async (
            string? path, bool? recursive, IFileManagerService files, IAuditLogStore audit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var result = files.Delete(path, recursive ?? false);
            if (result.Status is FileOpStatus.Ok)
                await audit.AddAsync(FileAudit("file.deleted", user, path!, recursive is true ? "recursive" : null), ct);
            return result.Status switch
            {
                FileOpStatus.Ok => Results.NoContent(),
                FileOpStatus.Conflict => Results.Problem(
                    $"Folder is not empty ({result.ItemCount} items). Pass recursive=true to delete it including its contents.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => Problem(result.Status),
            };
        })
        .WithSummary("Delete a file or folder (a non-empty folder requires recursive=true)")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPatch("/rename", async (
            RenameFileRequest request, IFileManagerService files, IAuditLogStore audit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var result = files.Rename(request.Path, request.NewName);
            if (result.Status is not FileOpStatus.Ok)
                return Problem(result.Status, result.Detail);
            if (!string.Equals(request.Path, result.Entry!.Path, StringComparison.Ordinal)) // no audit for a no-op rename
                await audit.AddAsync(FileAudit("file.renamed", user, request.Path, $"→ {result.Entry.Path}"), ct);
            return Results.Ok(FileItemDto.From(result.Entry!));
        })
        .WithSummary("Rename a file or folder (files must keep the .pdf extension)")
        .Produces<FileItemDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/move", async (
            MoveFileRequest request, IFileManagerService files, IAuditLogStore audit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var result = files.Move(request.SourcePath, request.TargetDirectory, request.Overwrite);
            if (result.Status is not FileOpStatus.Ok)
                return Problem(result.Status, result.Detail);
            await audit.AddAsync(FileAudit("file.moved", user, request.SourcePath, $"→ {result.Entry!.Path}"), ct);
            return Results.Ok(FileItemDto.From(result.Entry!));
        })
        .WithSummary("Move a file or folder into a target folder (name kept; folder collisions are never merged)")
        .Produces<FileItemDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/copy", async (
            CopyFileRequest request, IFileManagerService files, IAuditLogStore audit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var result = files.Copy(request.SourcePath, request.TargetDirectory, request.Overwrite, ct);
            if (result.Status is not FileOpStatus.Ok)
                return Problem(result.Status, result.Detail);
            await audit.AddAsync(FileAudit("file.copied", user, request.SourcePath, $"→ {result.Entry!.Path}"), ct);
            return Results.Ok(FileItemDto.From(result.Entry!));
        })
        .WithSummary("Copy a file or folder into a target folder (folder copy = subfolders + PDFs)")
        .Produces<FileItemDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/search", (string? path, string? query, bool? recursive, IFileManagerService files, CancellationToken ct) =>
        {
            var result = files.Search(path, query, recursive ?? true, ct);
            return result.Status is FileOpStatus.Ok
                ? Results.Ok(new FileSearchResponse
                {
                    Query = result.Query,
                    SearchPath = result.SearchPath,
                    Items = result.Items!.Select(FileItemDto.From).ToList(),
                    Truncated = result.Truncated,
                })
                : Problem(result.Status, result.Detail);
        })
        .WithSummary("Find PDFs by name under a folder (recursive by default; result capped by MaxSearchResults)")
        .Produces<FileSearchResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // Diagnostics for the client's configuration page — always 200; "not configured" is a state, not an error.
        group.MapGet("/status", (IFileManagerService files) =>
            Results.Ok(FileManagerStatusResponse.From(files.GetStatus())))
        .WithSummary("Storage diagnostics: effective root + its source, availability, writability, free space and limits")
        .Produces<FileManagerStatusResponse>();

        group.MapGet("/download", (string? path, IFileManagerService files) =>
        {
            var result = files.ResolveDownload(path);
            return result.Status is FileOpStatus.Ok
                ? Results.File(result.AbsolutePath!, "application/pdf", result.FileName, enableRangeProcessing: true)
                : Problem(result.Status);
        })
        .WithSummary("Download a PDF file")
        .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Append-only audit trail for destructive file operations (virtual paths only, never absolute).</summary>
    private static AuditEntry FileAudit(string action, ClaimsPrincipal user, string path, string? detail) => new()
    {
        Id = Guid.NewGuid(),
        Actor = user.Actor(),
        Source = nameof(JobSource.Manager),
        Action = action,
        EntityType = "file",
        EntityId = path,
        Detail = detail,
    };

    /// <summary>Maps a non-Ok service outcome to ProblemDetails (no internal paths in any message).</summary>
    private static IResult Problem(FileOpStatus status, string? detail = null) => status switch
    {
        FileOpStatus.RootNotConfigured => Results.Problem(
            "File manager root is not configured (FileManager:RootPath or ScopeSync:RootPath).",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        FileOpStatus.InvalidPath => Results.Problem(detail ?? "Invalid path.", statusCode: StatusCodes.Status400BadRequest),
        FileOpStatus.InvalidName => Results.Problem(detail ?? "Invalid name.", statusCode: StatusCodes.Status400BadRequest),
        FileOpStatus.NotFound => Results.Problem(detail ?? "File or folder not found.", statusCode: StatusCodes.Status404NotFound),
        FileOpStatus.Conflict => Results.Problem(detail ?? "The name already exists.", statusCode: StatusCodes.Status409Conflict),
        FileOpStatus.NotAFile => Results.Problem("The path is a folder, not a file.", statusCode: StatusCodes.Status400BadRequest),
        FileOpStatus.NotAFolder => Results.Problem("The path is a file, not a folder.", statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem("File operation failed.", statusCode: StatusCodes.Status500InternalServerError),
    };
}
