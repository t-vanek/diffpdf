using DiffPdf.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DiffPdf.Notifications;

/// <summary>Sends the notification as a plain-text e-mail via the configured SMTP server (MailKit).</summary>
public sealed class SmtpNotifier(IOptions<NotificationOptions> options, ILogger<SmtpNotifier> logger) : INotifier
{
    public string Channel => "smtp";

    public async Task SendAsync(NotificationSubscription subscription, INotification notification, CancellationToken ct)
    {
        var smtp = options.Value.Smtp;
        if (smtp is null || string.IsNullOrWhiteSpace(smtp.Host))
        {
            logger.LogWarning("SMTP channel used but Notifications:Smtp is not configured; skipping e-mail to {Target}.", subscription.Target);
            return;
        }
        if (string.IsNullOrWhiteSpace(subscription.Target))
        {
            logger.LogWarning("SMTP subscription has no target address; skipping.");
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(smtp.From));
        message.To.Add(MailboxAddress.Parse(subscription.Target));
        message.Subject = notification.Title;
        message.Body = new TextPart("plain") { Text = notification.Summary };

        // UseSsl=true lets MailKit pick the right transport security for the port (implicit SSL on 465,
        // STARTTLS otherwise); false connects in plain text (a local/relay SMTP without TLS).
        var security = smtp.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(smtp.Username))
            await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
