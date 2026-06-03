using System.Net.Http;
using System.Net.Http.Json;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Fetches + caches an OAuth2 client-credentials bearer token for the SignalR hub handshake (the REST
/// client gets its token from the SDK's own handler). Returns null when no credentials are configured
/// (dev mode, auth disabled), so the hub connects anonymously.
/// </summary>
public sealed class TokenSource(ServerSession session)
{
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string?> GetTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(session.ClientId) || session.BaseUrl is null)
            return null;

        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        using var http = new HttpClient { BaseAddress = new Uri(session.BaseUrl) };
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = session.ClientId!,
            ["client_secret"] = session.ClientSecret ?? string.Empty,
        });

        using var resp = await http.PostAsync("/connect/token", form);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>();

        _token = token!.AccessToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, token.ExpiresIn - 30));
        return _token;
    }
}
