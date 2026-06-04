using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Configuration;

public static class ScopeConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-scope configuration resolver, CRUD service and auto-provisioner. The
    /// <see cref="IScopeConfigurationStore"/> is provided by the persistence layer (relational or in-memory).
    /// </summary>
    public static IServiceCollection AddDiffPdfScopeConfiguration(this IServiceCollection services)
    {
        services.AddScoped<IScopeConfigurationResolver, ScopeConfigurationResolver>();
        services.AddScoped<IScopeConfigurationService, ScopeConfigurationService>();
        services.AddScoped<IScopeConfigurationProvisioner, ScopeConfigurationProvisioner>();
        return services;
    }
}
