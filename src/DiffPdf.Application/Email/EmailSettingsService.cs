using System.Net.Mail;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Email;

/// <summary>Fields for saving the SMTP settings. A blank <see cref="Password"/> keeps the stored one (the GET never
/// returns the secret, so a round-tripped save must not wipe it).</summary>
public sealed record EmailSettingsInput(
    string Host,
    int Port,
    bool UseSsl,
    string? Username,
    string? Password,
    string FromAddress,
    string? FromName);

/// <summary>Invalid SMTP settings (empty host, bad port, invalid From). Maps to 400 at the endpoint.</summary>
public sealed class EmailSettingsValidationException(string message) : Exception(message);

/// <summary>Runtime SMTP settings: read the single row (null until first saved) and upsert it (password-preserving).</summary>
public interface IEmailSettingsService
{
    Task<EmailSettings?> GetAsync(CancellationToken ct = default);
    Task<EmailSettings> SaveAsync(EmailSettingsInput input, long version, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class EmailSettingsService(IEmailSettingsStore store) : IEmailSettingsService
{
    public Task<EmailSettings?> GetAsync(CancellationToken ct = default) => store.GetAsync(ct);

    public async Task<EmailSettings> SaveAsync(EmailSettingsInput input, long version, CancellationToken ct = default)
    {
        Validate(input);
        var existing = await store.GetAsync(ct);
        // Keep the stored password when none is submitted (the GET masks it, so the editor sends it back blank).
        string? password = string.IsNullOrEmpty(input.Password) ? existing?.Password : input.Password;

        var settings = new EmailSettings
        {
            Host = input.Host.Trim(),
            Port = input.Port,
            UseSsl = input.UseSsl,
            Username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim(),
            Password = password,
            FromAddress = input.FromAddress.Trim(),
            FromName = string.IsNullOrWhiteSpace(input.FromName) ? null : input.FromName.Trim(),
        };
        return await store.SaveAsync(settings, version, ct);
    }

    private static void Validate(EmailSettingsInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Host))
            throw new EmailSettingsValidationException("SMTP host must not be empty.");
        if (input.Port is < 1 or > 65535)
            throw new EmailSettingsValidationException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(input.FromAddress) || !MailAddress.TryCreate(input.FromAddress, out _))
            throw new EmailSettingsValidationException("From address must be a valid e-mail address.");
    }
}
