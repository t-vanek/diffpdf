using DiffPdf.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Notifications.DependencyInjection;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbound e-mail notification dispatcher and its SMTP sender. The effective SMTP settings are
    /// resolved at runtime from the store (with the legacy <c>Notifications:Smtp</c> appsettings as a fallback);
    /// only <c>BaseUrl</c> + that fallback bind from the <c>Notifications</c> section. The rules themselves are
    /// runtime-managed via the API (the store). Safe to call always: with no enabled rules the dispatcher is a
    /// no-op. The dispatcher + resolver are Scoped because they read the scoped stores.
    /// </summary>
    public static IServiceCollection AddDiffPdfNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<EmailSettingsResolver>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        return services;
    }
}
