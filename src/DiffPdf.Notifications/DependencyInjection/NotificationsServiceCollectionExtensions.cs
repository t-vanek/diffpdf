using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Notifications.DependencyInjection;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbound notification dispatcher and its channels (webhook + SMTP),
    /// bound from the <c>Notifications</c> configuration section. Safe to call always:
    /// when <c>Notifications:Enabled</c> is false the dispatcher is a no-op.
    /// </summary>
    public static IServiceCollection AddDiffPdfNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.AddHttpClient(WebhookNotifier.HttpClientName);
        services.AddSingleton<INotifier, WebhookNotifier>();
        services.AddSingleton<INotifier, SmtpNotifier>();
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
        return services;
    }
}
