using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Notifications;

/// <summary>Sends the notification as a plain-text e-mail via the configured SMTP server.</summary>
public sealed class SmtpNotifier(IOptions<NotificationOptions> options, ILogger<SmtpNotifier> logger) : INotifier
{
    public string Channel => "smtp";

    public async Task SendAsync(NotificationSubscription subscription, BatchNotification notification, CancellationToken ct)
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

        using var message = new MailMessage(smtp.From, subscription.Target)
        {
            Subject = notification.Title,
            Body = notification.Summary,
        };
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl };
        if (!string.IsNullOrWhiteSpace(smtp.Username))
            client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);

        await client.SendMailAsync(message, ct);
    }
}
