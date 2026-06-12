using DiffPdf.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace DiffPdf.Notifications;

/// <summary>One file to attach to an outgoing e-mail: the name shown in the mail client and the local file to read.</summary>
public sealed record EmailAttachment(string FileName, string FilePath);

/// <summary>Sends a plain-text e-mail to one or more recipients using the supplied <see cref="EmailSettings"/>.</summary>
public interface IEmailSender
{
    /// <summary>Connects with the given SMTP settings and sends <paramref name="subject"/>/<paramref name="body"/>
    /// (plus optional file <paramref name="attachments"/>) to each recipient. Throws on connect/auth/send failure
    /// so callers (e.g. the test endpoint) can surface it; the dispatcher wraps the call and logs instead.</summary>
    Task SendAsync(EmailSettings settings, IEnumerable<string> recipients, string subject, string body,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default);
}

/// <summary>MailKit-backed SMTP sender. Opens one connection per call and sends an individual message to each
/// recipient (so addresses are not exposed to each other and a per-recipient failure is isolated).</summary>
public sealed class SmtpEmailSender(ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailSettings settings, IEnumerable<string> recipients, string subject, string body,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("SMTP server is not configured (set it in Konfigurace → E-mail).");

        var from = string.IsNullOrWhiteSpace(settings.FromName)
            ? MailboxAddress.Parse(settings.FromAddress)
            : new MailboxAddress(settings.FromName, settings.FromAddress);

        // UseSsl=true lets MailKit pick the right transport security for the port (implicit SSL on 465, STARTTLS
        // otherwise); false connects in plain text (a local/relay SMTP without TLS).
        var security = settings.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, security, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.Username))
                await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, ct);

            // The MIME body (incl. any attachments) is identical for every recipient — build it once and reuse it.
            MimeEntity mimeBody;
            if (attachments is { Count: > 0 })
            {
                var builder = new BodyBuilder { TextBody = body };
                foreach (var attachment in attachments)
                    builder.Attachments.Add(attachment.FileName, await File.ReadAllBytesAsync(attachment.FilePath, ct));
                mimeBody = builder.ToMessageBody();
            }
            else
            {
                mimeBody = new TextPart("plain") { Text = body };
            }

            foreach (var to in recipients)
            {
                if (string.IsNullOrWhiteSpace(to)) continue;
                var message = new MimeMessage();
                message.From.Add(from);
                message.To.Add(MailboxAddress.Parse(to));
                message.Subject = subject;
                message.Body = mimeBody;
                await client.SendAsync(message, ct);
                logger.LogDebug("Sent notification e-mail to {To} (subject: {Subject}).", to, subject);
            }
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
    }
}
