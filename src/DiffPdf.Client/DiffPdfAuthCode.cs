using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace DiffPdf.Client;

/// <summary>Tuning for the interactive authorization-code + PKCE flow.</summary>
public sealed class AuthorizationCodeFlowOptions
{
    /// <summary>
    /// Loopback redirect URI to capture the authorization response. Must be registered
    /// on the server's interactive client (Auth:RedirectUris) and be a loopback address.
    /// </summary>
    public string RedirectUri { get; set; } = "http://localhost:7890/";

    /// <summary>How to open the authorization URL; defaults to the system browser.</summary>
    public Func<string, Task>? OpenBrowser { get; set; }

    /// <summary>How long to wait for the user to complete the login.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>HTML shown in the browser once the redirect is captured.</summary>
    public string ResponseHtml { get; set; } =
        "<!doctype html><html><body style='font-family:system-ui'>Přihlášení dokončeno — okno můžeš zavřít.</body></html>";
}

/// <summary>Runs the OAuth2 authorization-code + PKCE flow over a loopback listener.</summary>
internal static class DiffPdfAuthCode
{
    public static async Task<TokenResponse> RunAsync(
        HttpClient http, Uri baseAddress, string clientId, string scope,
        AuthorizationCodeFlowOptions? options, CancellationToken ct)
    {
        options ??= new AuthorizationCodeFlowOptions();
        string redirectUri = options.RedirectUri.EndsWith('/') ? options.RedirectUri : options.RedirectUri + "/";

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        string authorizeUrl =
            $"{baseAddress.GetLeftPart(UriPartial.Authority)}/connect/authorize" +
            $"?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var open = options.OpenBrowser ?? DefaultOpenBrowser;
        await open(authorizeUrl);

        string code = await WaitForCodeAsync(listener, state, options, ct);
        listener.Stop();

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };

        using var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            throw new DiffPdfClientException(response.StatusCode, await response.Content.ReadAsStringAsync(ct));

        return await response.Content.ReadFromJsonAsync<TokenResponse>(DiffPdfClient.Json, ct)
            ?? throw new DiffPdfClientException(response.StatusCode, "Empty token response.");
    }

    private static async Task<string> WaitForCodeAsync(
        HttpListener listener, string expectedState, AuthorizationCodeFlowOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the authorization redirect.");
        }

        var query = context.Request.QueryString;
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(options.ResponseHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, ct);
        }
        finally
        {
            context.Response.Close();
        }

        string? error = query["error"];
        if (!string.IsNullOrEmpty(error))
            throw new DiffPdfClientException(HttpStatusCode.Unauthorized, $"Authorization failed: {error}.");

        if (!string.Equals(query["state"], expectedState, StringComparison.Ordinal))
            throw new DiffPdfClientException(HttpStatusCode.BadRequest, "Authorization state mismatch (possible CSRF).");

        return query["code"] ?? throw new DiffPdfClientException(HttpStatusCode.BadRequest, "No authorization code in the redirect.");
    }

    private static Task DefaultOpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
