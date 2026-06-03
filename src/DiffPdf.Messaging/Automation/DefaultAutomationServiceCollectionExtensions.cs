using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Automation;

public static class DefaultAutomationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default-automation provisioner: when an instance is registered for the first time
    /// (scope sync or the create-instance API), it creates a disabled default schedule + watch and an
    /// optional one-time initial trigger. Bound from the <c>DefaultAutomation</c> configuration section
    /// and a no-op at runtime unless <c>DefaultAutomation:Enabled</c> is true. Requires
    /// <c>AddDiffPdfScheduling</c> (provides <see cref="Scheduling.IBatchLauncher"/>).
    /// </summary>
    public static IServiceCollection AddDiffPdfDefaultAutomation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DefaultAutomationOptions>(configuration.GetSection(DefaultAutomationOptions.SectionName));
        services.AddScoped<IDefaultAutomationProvisioner, DefaultAutomationProvisioner>();
        return services;
    }
}
