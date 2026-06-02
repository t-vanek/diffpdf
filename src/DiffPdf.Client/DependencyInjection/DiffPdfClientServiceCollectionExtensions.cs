using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Client.DependencyInjection;

/// <summary>DI registration for the diffpdf client SDK.</summary>
public static class DiffPdfClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DiffPdfClient"/> as a typed <c>HttpClient</c> (pooled via
    /// <see cref="IHttpClientFactory"/>) targeting <paramref name="baseAddress"/>, plus
    /// the discovery client. Resolve <see cref="DiffPdfClient"/> and authenticate it.
    /// </summary>
    public static IServiceCollection AddDiffPdfClient(this IServiceCollection services, Uri baseAddress)
    {
        services.AddHttpClient<DiffPdfClient>(c => c.BaseAddress = baseAddress);
        services.AddSingleton<IDiffPdfDiscoveryClient, DiffPdfDiscoveryClient>();
        return services;
    }

    /// <summary>Registers only the network discovery client (for locating a server before its URL is known).</summary>
    public static IServiceCollection AddDiffPdfDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<IDiffPdfDiscoveryClient, DiffPdfDiscoveryClient>();
        return services;
    }
}
