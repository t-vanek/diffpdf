using DiffPdf.Application.Email;
using DiffPdf.Core.Storage;
using DiffPdf.Notifications;

namespace DiffPdf.Api.Endpoints;

/// <summary>
/// Runtime SMTP settings (single row) + a test-send. The password is write-only: GET returns only
/// <c>HasPassword</c>, and PUT keeps the stored password when none is supplied. The test-send uses the
/// effective settings (the saved row, or the legacy <c>Notifications:Smtp</c> fallback).
/// </summary>
public static class EmailSettingsEndpoints
{
    public static void MapEmailSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/email").WithTags("EmailSettings");

        group.MapGet("/", async (IEmailSettingsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(ct) is { } s ? EmailSettingsResponse.From(s) : EmailSettingsResponse.Empty()))
            .WithSummary("Get SMTP settings (password masked)").Produces<EmailSettingsResponse>();

        group.MapPut("/", (UpdateEmailSettingsRequest request, IEmailSettingsService svc, CancellationToken ct) =>
            Run(async () =>
            {
                var saved = await svc.SaveAsync(
                    new EmailSettingsInput(request.Host, request.Port, request.UseSsl, request.Username,
                        request.Password, request.FromAddress, request.FromName),
                    request.Version, ct);
                return Results.Ok(EmailSettingsResponse.From(saved));
            }))
            .WithSummary("Save SMTP settings (optimistic concurrency via Version)")
            .Produces<EmailSettingsResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/test", async (SendTestEmailRequest request, EmailSettingsResolver resolver, IEmailSender sender, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.To))
                    return Results.Problem("Recipient address is required.", statusCode: StatusCodes.Status400BadRequest);

                var settings = await resolver.ResolveAsync(ct);
                if (settings is not { IsConfigured: true })
                    return Results.Problem("SMTP is not configured. Save the e-mail settings first.",
                        statusCode: StatusCodes.Status400BadRequest);

                try
                {
                    await sender.SendAsync(settings, [request.To],
                        "diffpdf — testovací e-mail",
                        "Toto je testovací zpráva z DiffPDF Remote Manager. SMTP nastavení funguje.", ct: ct);
                    return Results.Ok(new { ok = true });
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Odeslání selhalo: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
                }
            })
            .WithSummary("Send a test e-mail with the saved SMTP settings")
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status502BadGateway);
    }

    /// <summary>Maps the service's validation/conflict outcomes to HTTP.</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (EmailSettingsValidationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (ConcurrencyConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}
