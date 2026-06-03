using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Client;

/// <summary>Registers <see cref="DiffPdfClient"/> as a typed <c>HttpClient</c>.</summary>
public static class DiffPdfClientServiceCollectionExtensions
{
    /// <summary>Registers the client pointing at <paramref name="baseAddress"/> (the API root), without authentication.</summary>
    public static IHttpClientBuilder AddDiffPdfClient(this IServiceCollection services, Uri baseAddress) =>
        services.AddHttpClient<DiffPdfClient>(c => c.BaseAddress = baseAddress)
            .AddHttpMessageHandler(() => new TransientRetryHandler());

    /// <summary>
    /// Registers the client with an M2M (OpenIddict client-credentials) bearer-token handler that
    /// fetches and caches a token from <c>/connect/token</c>.
    /// </summary>
    public static IHttpClientBuilder AddDiffPdfClient(
        this IServiceCollection services, Uri baseAddress, string clientId, string clientSecret, string? scope = null) =>
        services.AddHttpClient<DiffPdfClient>(c => c.BaseAddress = baseAddress)
            // Retry is outermost so each attempt re-runs the token handler (refreshing if expired).
            .AddHttpMessageHandler(() => new TransientRetryHandler())
            .AddHttpMessageHandler(() => new ClientCredentialsTokenHandler(baseAddress, clientId, clientSecret, scope));
}
