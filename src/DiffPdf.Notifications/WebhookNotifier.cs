using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Notifications;

/// <summary>
/// Posts the notification as JSON to a configured URL. The payload carries a Slack/Teams
/// compatible <c>text</c> field plus the full structured detail under <c>diffpdf</c>.
/// </summary>
public sealed class WebhookNotifier(IHttpClientFactory httpFactory, ILogger<WebhookNotifier> logger) : INotifier
{
    public const string HttpClientName = "diffpdf-notifications";

    public string Channel => "webhook";

    public async Task SendAsync(NotificationSubscription subscription, BatchNotification notification, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subscription.Target))
        {
            logger.LogWarning("Webhook subscription has no target URL; skipping.");
            return;
        }

        var client = httpFactory.CreateClient(HttpClientName);
        var payload = new
        {
            text = $"{notification.Title}\n{notification.Summary}",
            diffpdf = notification,
        };

        // Connection-level failures bubble (HttpRequestException) so Wolverine can retry the
        // event; a non-2xx response is logged but not retried (avoids hammering a bad endpoint).
        using var response = await client.PostAsJsonAsync(subscription.Target, payload, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("Webhook to {Target} returned {Status} for job {JobId}.",
                subscription.Target, (int)response.StatusCode, notification.JobId);
    }
}
