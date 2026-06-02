using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace DiffPdf.Api.Tests;

/// <summary>
/// Exercises the OpenIddict flows end-to-end against <see cref="AuthTestServer"/>:
/// client-credentials, and the full interactive authorization-code + PKCE + refresh
/// + revocation dance (including the cookie login and antiforgery).
/// </summary>
public sealed class AuthFlowTests : IAsyncLifetime
{
    private readonly AuthTestServer _server = new();

    public Task InitializeAsync() => _server.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    private HttpClient NewClient() => new(
        new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(_server.BaseUrl),
    };

    [Fact]
    public async Task ClientCredentials_IssuesUsableToken()
    {
        using var http = NewClient();

        var token = await PostFormAsync(http, "/connect/token", new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "diffpdf-ci",
            ["client_secret"] = "secret",
            ["scope"] = "diffpdf.api",
        });

        Assert.Equal(HttpStatusCode.OK, token.Status);
        string access = token.Json.GetProperty("access_token").GetString()!;
        Assert.False(string.IsNullOrEmpty(access));

        http.DefaultRequestHeaders.Authorization = new("Bearer", access);
        using var protectedResponse = await http.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task ClientCredentials_WrongSecret_IsRejected()
    {
        using var http = NewClient();
        var token = await PostFormAsync(http, "/connect/token", new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "diffpdf-ci",
            ["client_secret"] = "nope",
            ["scope"] = "diffpdf.api",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, token.Status);
    }

    [Fact]
    public async Task AuthorizationCode_Pkce_Refresh_Revocation_FullFlow()
    {
        using var http = NewClient();

        var (verifier, challenge) = Pkce();
        string state = Guid.NewGuid().ToString("N");
        string authorizeUrl =
            "/connect/authorize?response_type=code&client_id=diffpdf-app" +
            $"&redirect_uri={Uri.EscapeDataString(_server.RedirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid profile offline_access diffpdf.api")}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";

        // 1) Unauthenticated authorize -> redirect to the login form.
        using var challengeResponse = await http.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, challengeResponse.StatusCode);
        string loginLocation = challengeResponse.Headers.Location!.ToString();
        Assert.Contains("/account/login", loginLocation);

        // 2) GET the login form (sets the antiforgery cookie) and read its token.
        using var loginForm = await http.GetAsync(loginLocation);
        Assert.Equal(HttpStatusCode.OK, loginForm.StatusCode);
        string html = await loginForm.Content.ReadAsStringAsync();
        string antiforgery = ExtractHidden(html, "__RequestVerificationToken");

        // 3) Submit credentials -> redirect back to the authorize URL, login cookie set.
        using var loginPost = await PostRawFormAsync(http, "/account/login", new()
        {
            ["__RequestVerificationToken"] = antiforgery,
            ["returnUrl"] = authorizeUrl,
            ["username"] = "tester",
            ["password"] = "pw",
        });
        Assert.Equal(HttpStatusCode.Redirect, loginPost.StatusCode);

        // 4) Authorize again (now authenticated) -> redirect to the client with a code.
        using var codeResponse = await http.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, codeResponse.StatusCode);
        var redirect = codeResponse.Headers.Location!;
        Assert.StartsWith(_server.RedirectUri, redirect.ToString());
        var query = QueryHelpers.ParseQuery(redirect.Query);
        Assert.Equal(state, query["state"].ToString());
        string code = query["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code));

        // 5) Exchange the code (with the PKCE verifier) for access + refresh tokens.
        var tokens = await PostFormAsync(http, "/connect/token", new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _server.RedirectUri,
            ["client_id"] = "diffpdf-app",
            ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        Assert.False(string.IsNullOrEmpty(tokens.Json.GetProperty("access_token").GetString()));
        string refresh = tokens.Json.GetProperty("refresh_token").GetString()!;
        Assert.False(string.IsNullOrEmpty(refresh));

        // 6) Use the refresh token to get a fresh access token.
        var refreshed = await PostFormAsync(http, "/connect/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "diffpdf-app",
            ["refresh_token"] = refresh,
        });
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.False(string.IsNullOrEmpty(refreshed.Json.GetProperty("access_token").GetString()));

        // 7) Revoke a token — the endpoint accepts the request.
        using var revoke = await PostRawFormAsync(http, "/connect/revocation", new()
        {
            ["token"] = refreshed.Json.GetProperty("refresh_token").GetString()!,
            ["client_id"] = "diffpdf-app",
        });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        using var http = NewClient();
        using var response = await PostRawFormAsync(http, "/account/login", new()
        {
            ["username"] = "tester",
            ["password"] = "pw",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- helpers -----------------------------------------------------------

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostFormAsync(
        HttpClient http, string url, Dictionary<string, string> form)
    {
        using var response = await http.PostAsync(url, new FormUrlEncodedContent(form));
        string body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static Task<HttpResponseMessage> PostRawFormAsync(HttpClient http, string url, Dictionary<string, string> form) =>
        http.PostAsync(url, new FormUrlEncodedContent(form));

    private static string ExtractHidden(string html, string fieldName)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(fieldName)}\"\\s+value=\"([^\"]*)\"");
        Assert.True(match.Success, $"Hidden field '{fieldName}' not found in login form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
