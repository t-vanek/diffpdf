using System.Net.Http;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Holds the live connection to a diffpdf server: the typed <see cref="DiffPdfClient"/> built from the
/// entered base URL (+ optional OAuth2 client-credentials via the SDK's token handler). Connection is
/// verified with a liveness probe. Raises <see cref="StateChanged"/> on connect/disconnect.
/// </summary>
public sealed class ServerSession
{
    public DiffPdfClient? Client { get; private set; }
    public string? BaseUrl { get; private set; }
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }

    public bool IsConnected => Client is not null;

    public event EventHandler? StateChanged;

    /// <summary>The connected client, or throws if not connected.</summary>
    public DiffPdfClient Require() =>
        Client ?? throw new InvalidOperationException("Není připojeno k serveru.");

    public async Task ConnectAsync(string baseUrl, string? clientId, string? clientSecret, CancellationToken ct = default)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);

        HttpClient http;
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            // OAuth2 client-credentials: the SDK handler fetches + caches the bearer token.
            var handler = new ClientCredentialsTokenHandler(uri, clientId!, clientSecret ?? string.Empty)
            {
                InnerHandler = new HttpClientHandler(),
            };
            http = new HttpClient(handler) { BaseAddress = uri };
        }
        else
        {
            http = new HttpClient { BaseAddress = uri };
        }

        var client = new DiffPdfClient(http);
        if (!await client.HealthAsync(ct))
        {
            http.Dispose();
            throw new InvalidOperationException($"Server {baseUrl} neodpovídá na /health.");
        }

        Client = client;
        BaseUrl = baseUrl;
        ClientId = clientId;
        ClientSecret = clientSecret;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        Client = null;
        BaseUrl = null;
        ClientId = null;
        ClientSecret = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
