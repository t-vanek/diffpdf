using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Notifications;

/// <summary>
/// Resolves the effective e-mail (SMTP) settings: the runtime-managed row from <see cref="IEmailSettingsStore"/>
/// if configured, otherwise the legacy <c>Notifications:Smtp</c> appsettings (so existing deployments keep working
/// until settings are saved from the UI). Returns null / a not-configured value when neither is set.
/// </summary>
public sealed class EmailSettingsResolver(IEmailSettingsStore store, IOptions<NotificationOptions> options)
{
    public async Task<EmailSettings?> ResolveAsync(CancellationToken ct = default)
    {
        var stored = await store.GetAsync(ct);
        if (stored is { IsConfigured: true })
            return stored;

        var smtp = options.Value.Smtp;
        if (smtp is not null && !string.IsNullOrWhiteSpace(smtp.Host))
            return new EmailSettings
            {
                Host = smtp.Host,
                Port = smtp.Port,
                UseSsl = smtp.UseSsl,
                Username = smtp.Username,
                Password = smtp.Password,
                FromAddress = smtp.From,
            };

        return stored; // null, or a stored-but-not-configured row
    }
}
