using DiffPdf.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Notifications.DependencyInjection;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbound notification dispatcher and its channels (webhook + SMTP).
    /// Only the e-mail transport / base URL bind from the <c>Notifications</c> configuration
    /// section; the subscriptions themselves are runtime-managed via the API (the store).
    /// Safe to call always: with no enabled subscriptions the dispatcher is a no-op. The
    /// dispatcher is Scoped because it reads the scoped <see cref="ISubscriptionStore"/>.
    /// </summary>
    public static IServiceCollection AddDiffPdfNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.AddHttpClient(WebhookNotifier.HttpClientName);
        services.AddSingleton<INotifier, WebhookNotifier>();
        services.AddSingleton<INotifier, SmtpNotifier>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        return services;
    }
}
