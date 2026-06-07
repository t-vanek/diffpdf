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
    public DiffPdfClient? Client { get; internal set; } // internal set: tests inject a fake-transport client
    public string? BaseUrl { get; private set; }
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }

    /// <summary>Whether the connected server requires auth (read from <c>/health</c>); drives credential-field visibility.</summary>
    public bool ServerRequiresAuth { get; private set; }

    public bool IsConnected => Client is not null;

    public event EventHandler? StateChanged;

    /// <summary>The connected client, or throws if not connected.</summary>
    public DiffPdfClient Require() =>
        Client ?? throw new InvalidOperationException("Není připojeno k serveru.");

    public async Task ConnectAsync(string baseUrl, string? clientId, string? clientSecret, CancellationToken ct = default)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);

        // Probe anonymously first: confirms liveness AND learns whether the server requires auth, so we
        // attach credentials only when they're actually needed (and the UI can hide them otherwise).
        ServerInfo? info;
        using (var probe = new HttpClient { BaseAddress = uri })
            info = await new DiffPdfClient(probe).GetServerInfoAsync(ct);

        if (info is null)
            throw new InvalidOperationException($"Server {baseUrl} neodpovídá na /health.");

        bool useAuth = info.AuthEnabled && !string.IsNullOrWhiteSpace(clientId);

        HttpClient http;
        if (useAuth)
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

        Client = new DiffPdfClient(http);
        BaseUrl = baseUrl;
        ClientId = useAuth ? clientId : null;
        ClientSecret = useAuth ? clientSecret : null;
        ServerRequiresAuth = info.AuthEnabled;
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
