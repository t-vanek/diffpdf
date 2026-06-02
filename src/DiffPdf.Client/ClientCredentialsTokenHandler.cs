using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DiffPdf.Client;

/// <summary>
/// <see cref="DelegatingHandler"/> that attaches an OAuth2 bearer token obtained via the
/// OpenIddict client-credentials grant, refreshing it shortly before expiry. Uses a
/// dedicated <see cref="HttpClient"/> for the token request so it never recurses through itself.
/// </summary>
public sealed class ClientCredentialsTokenHandler : DelegatingHandler
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string? _scope;
    private readonly HttpClient _tokenHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt;

    public ClientCredentialsTokenHandler(Uri baseAddress, string clientId, string clientSecret, string? scope = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scope = scope;
        _tokenHttp = new HttpClient { BaseAddress = baseAddress };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        return await base.SendAsync(request, ct);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
            };
            if (_scope is not null) form["scope"] = _scope;

            using var resp = await _tokenHttp.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);
            resp.EnsureSuccessStatusCode();
            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(DiffPdfClient.Json, ct)
                ?? throw new InvalidOperationException("Empty token response from /connect/token.");

            _token = token.AccessToken;
            // Refresh ~30s before the server-stated expiry.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, token.ExpiresIn - 30));
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tokenHttp.Dispose();
            _gate.Dispose();
        }
        base.Dispose(disposing);
    }
}
