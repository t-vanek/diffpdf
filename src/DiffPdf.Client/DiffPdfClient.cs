using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffPdf.Core.Discovery;
using DiffPdf.Core.Models;

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

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    // Cached client-credentials for transparent re-authentication.
    private string? _clientId;
    private string? _clientSecret;
    private string? _scope;
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
    /// Authenticates with the client-credentials grant and applies the bearer token.
    /// Credentials are cached so the token is refreshed automatically before it expires.
    /// </summary>
    public async Task AuthenticateClientCredentialsAsync(
        string clientId, string clientSecret, string? scope = null, CancellationToken ct = default)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scope = scope;
        await AcquireTokenAsync(ct);
    }

    private async Task AcquireTokenAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
        };
        if (!string.IsNullOrEmpty(_scope))
            form["scope"] = _scope;

        using var response = await _http.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);
        await EnsureSuccessAsync(response, ct);

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
            ?? throw new DiffPdfClientException(response.StatusCode, "Empty token response.");

        _accessToken = token.AccessToken;
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
        Func<Task<string?>>? tokenProvider = _clientId is null
            ? null
            : async () => { await EnsureTokenAsync(CancellationToken.None); return _accessToken; };
        return DiffPdfLiveProgress.ConnectAsync(hubUrl, tokenProvider, ct);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_clientId is not null && DateTimeOffset.UtcNow >= _tokenExpiresAt)
            await AcquireTokenAsync(ct);
    }

    // --- Scope management --------------------------------------------------

    public Task CreateBusinessInstanceAsync(string key, string name, CancellationToken ct = default) =>
        PostAsync("/api/v1/business-instances", new { key, name }, ct);

    public Task CreateProjectAsync(string businessInstanceKey, string key, string name, CancellationToken ct = default) =>
        PostAsync($"/api/v1/business-instances/{businessInstanceKey}/projects", new { key, name }, ct);

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
        throw new DiffPdfClientException(response.StatusCode, string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "" : body);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
