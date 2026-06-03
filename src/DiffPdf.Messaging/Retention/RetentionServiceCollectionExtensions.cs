using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Retention;

public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the artifact-retention background service, bound from the <c>Retention</c> section.
    /// Safe to call always: when <c>Retention:Enabled</c> is false the service exits immediately.
    /// </summary>
    public static IServiceCollection AddDiffPdfRetention(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.AddHostedService<RetentionService>();
        return services;
    }
}
