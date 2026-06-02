using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffPdf.Core.Discovery;
using DiffPdf.Core.Models;
using DiffPdf.Core.Preview;

namespace DiffPdf.Client;

/// <summary>
/// Typed REST client for remotely controlling a diffpdf server: authentication,
/// scope/batch management, job polling, reports and artifact download. Pair it
/// with <see cref="DiffPdfDiscoveryClient"/> to locate the server first and
/// <see cref="DiffPdfLiveProgress"/> for realtime progress.
/// </summary>
public sealed class DiffPdfClient : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private enum Grant { None, ClientCredentials, AuthorizationCode }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Cached grant state for transparent re-authentication.
    private Grant _grant;
    private string? _clientId;
    private string? _clientSecret;
    private string? _scope;
    private string? _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    public DiffPdfClient(HttpClient http, bool ownsHttp = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
    }

    /// <summary>Base URL the client targets (e.g. <c>http://192.168.1.10:8080</c>).</summary>
    public Uri BaseAddress => _http.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress is not set.");

    /// <summary>Creates a client for a server located via discovery.</summary>
    public static DiffPdfClient ForServer(DiffPdfServerDescriptor descriptor) =>
        ForBaseUrl(descriptor.BaseUrl);

    /// <summary>Creates a client for a base URL.</summary>
    public static DiffPdfClient ForBaseUrl(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) }, ownsHttp: true);

    // --- Server info -------------------------------------------------------

    public Task<DiffPdfServerDescriptor> GetServerInfoAsync(CancellationToken ct = default) =>
        GetAsync<DiffPdfServerDescriptor>("/api/v1/server-info", ct);

    // --- Authentication ----------------------------------------------------

    /// <summary>
    /// Authenticates with the client-credentials grant (M2M) and applies the bearer
    /// token. Credentials are cached so the token is refreshed automatically before
    /// it expires.
    /// </summary>
    public async Task AuthenticateClientCredentialsAsync(
        string clientId, string clientSecret, string? scope = null, CancellationToken ct = default)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            _grant = Grant.ClientCredentials;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _scope = scope;
            ApplyToken(await PostTokenAsync(ClientCredentialsForm(), ct), response: null);
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>
    /// Authenticates a user with the interactive authorization-code + PKCE flow:
    /// opens the system browser, captures the redirect on a loopback listener, and
    /// exchanges the code for access + refresh tokens (refreshed automatically).
    /// The interactive client must have the redirect URI registered (Auth:RedirectUris).
    /// </summary>
    public async Task AuthenticateAuthorizationCodeAsync(
        string clientId, string scope, AuthorizationCodeFlowOptions? options = null, CancellationToken ct = default)
    {
        var token = await DiffPdfAuthCode.RunAsync(_http, BaseAddress, clientId, scope, options, ct);
        await _tokenLock.WaitAsync(ct);
        try
        {
            _grant = Grant.AuthorizationCode;
            _clientId = clientId;
            _scope = scope;
            ApplyToken(token, response: null);
        }
        finally { _tokenLock.Release(); }
    }

    private Dictionary<string, string> ClientCredentialsForm()
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
        };
        if (!string.IsNullOrEmpty(_scope))
            form["scope"] = _scope;
        return form;
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
            ?? throw new DiffPdfClientException(response.StatusCode, "Empty token response.");
    }

    private void ApplyToken(TokenResponse token, HttpResponseMessage? response)
    {
        _ = response;
        _accessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken))
            _refreshToken = token.RefreshToken;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        // Refresh a little ahead of expiry to avoid races.
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, token.ExpiresIn - 30));
    }

    /// <summary>Current bearer access token (null until authenticated).</summary>
    public string? AccessToken => _accessToken;

    /// <summary>
    /// Opens a realtime job-progress connection to this server's hub, forwarding the
    /// current bearer token (refreshed on demand) to the hub.
    /// </summary>
    public Task<DiffPdfLiveProgress> ConnectLiveProgressAsync(CancellationToken ct = default)
    {
        string hubUrl = new Uri(BaseAddress, "/hubs/jobs").ToString();
        Func<Task<string?>>? tokenProvider = _grant == Grant.None
            ? null
            : async () => { await EnsureTokenAsync(CancellationToken.None); return _accessToken; };
        return DiffPdfLiveProgress.ConnectAsync(hubUrl, tokenProvider, ct);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        // Fast path: still valid (or never authenticated).
        if (_grant == Grant.None || DateTimeOffset.UtcNow < _tokenExpiresAt)
            return;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock so concurrent callers coalesce into one refresh.
            if (DateTimeOffset.UtcNow < _tokenExpiresAt)
                return;

            var form = _grant switch
            {
                Grant.ClientCredentials => ClientCredentialsForm(),
                Grant.AuthorizationCode => new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _clientId!,
                    ["refresh_token"] = _refreshToken!,
                },
                _ => null,
            };
            if (form is null)
                return;

            ApplyToken(await PostTokenAsync(form, ct), response: null);
        }
        finally { _tokenLock.Release(); }
    }

    // --- Scope management --------------------------------------------------

    public Task CreateBusinessInstanceAsync(string key, string name, CancellationToken ct = default) =>
        PostAsync("/api/v1/business-instances", new { key, name }, ct);

    public Task CreateProjectAsync(string businessInstanceKey, string key, string name, CancellationToken ct = default) =>
        PostAsync($"/api/v1/business-instances/{businessInstanceKey}/projects", new { key, name }, ct);

    // --- Batch preview (dry-run) ------------------------------------------

    /// <summary>Lists the server's configured shares and credential-profile names (no secrets).</summary>
    public Task<NetworkConfig> GetSharesAsync(CancellationToken ct = default) =>
        GetAsync<NetworkConfig>("/api/v1/preview/shares", ct);

    /// <summary>Probes a folder (local, UNC or <c>share:</c> alias) for reachability and PDF count.</summary>
    public Task<FolderInspection> InspectFolderAsync(
        string folder, string? credentialProfile = null, string searchPattern = "*.pdf", bool recursive = true, CancellationToken ct = default) =>
        PostAsync<object, FolderInspection>("/api/v1/preview/folder",
            new { folder, credentialProfile, searchPattern, recursive }, ct);

    /// <summary>Dry-runs an old/new folder pairing without comparing.</summary>
    public Task<PairingPreview> PreviewPairingAsync(
        string oldFolder, string newFolder, string searchPattern = "*.pdf", bool recursive = true, CancellationToken ct = default) =>
        PostAsync<object, PairingPreview>("/api/v1/preview/pairing",
            new { oldFolder, newFolder, searchPattern, recursive }, ct);

    // --- Batch / jobs ------------------------------------------------------

    /// <summary>Submits a folder-comparison batch and returns the created job.</summary>
    public Task<DiffPdfJob> SubmitBatchAsync(BatchComparisonRequest request, CancellationToken ct = default) =>
        PostAsync<BatchComparisonRequest, DiffPdfJob>("/api/v1/batch", request, ct);

    public Task<DiffPdfJob> GetJobAsync(Guid jobId, CancellationToken ct = default) =>
        GetAsync<DiffPdfJob>($"/api/v1/jobs/{jobId}", ct);

    /// <summary>Polls until the job reaches a terminal state, reporting each update.</summary>
    public async Task<DiffPdfJob> WaitForJobAsync(
        Guid jobId, IProgress<DiffPdfJob>? progress = null, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            var job = await GetJobAsync(jobId, ct);
            progress?.Report(job);
            if (job.IsTerminal)
                return job;
            await Task.Delay(interval, ct);
        }
    }

    /// <summary>Fetches the aggregate report (call after the job completes).</summary>
    public Task<BatchComparisonReport> GetReportAsync(Guid jobId, CancellationToken ct = default) =>
        GetAsync<BatchComparisonReport>($"/api/v1/jobs/{jobId}/report", ct);

    public Task<DiffPdfJob> CancelJobAsync(Guid jobId, CancellationToken ct = default) =>
        PostAsync<DiffPdfJob>($"/api/v1/jobs/{jobId}/cancel", ct);

    public Task RetryJobAsync(Guid jobId, CancellationToken ct = default) =>
        PostAsync($"/api/v1/jobs/{jobId}/retry", content: null, ct);

    /// <summary>Downloads a highlighted diff-PDF artifact as a stream.</summary>
    public async Task<Stream> DownloadArtifactAsync(Guid jobId, string relativePath, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var response = await _http.GetAsync($"/api/v1/jobs/{jobId}/artifacts/{relativePath}", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    // --- HTTP helpers ------------------------------------------------------

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task PostAsync(string url, object? content, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var response = await _http.PostAsJsonAsync(url, content, Json, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<TResult> PostAsync<TResult>(string url, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var response = await _http.PostAsync(url, content: null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TResult>(Json, ct))!;
    }

    private async Task<TResult> PostAsync<TBody, TResult>(string url, TBody body, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var response = await _http.PostAsJsonAsync(url, body, Json, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TResult>(Json, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = await response.Content.ReadAsStringAsync(ct);
        throw new DiffPdfClientException(response.StatusCode, ExtractMessage(body, response.ReasonPhrase));
    }

    /// <summary>Surfaces a ProblemDetails <c>detail</c>/<c>title</c> when present, else the raw body.</summary>
    private static string ExtractMessage(string body, string? reasonPhrase)
    {
        if (string.IsNullOrWhiteSpace(body))
            return reasonPhrase ?? string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    return detail.GetString()!;
                if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    return title.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall back to the raw body.
        }

        return body;
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }
}
