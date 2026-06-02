using System.Security.Claims;
using System.Text.Json.Serialization;
using DiffPdf.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Api.Tests;

/// <summary>
/// A minimal, self-contained host that wires the real auth stack
/// (<see cref="AuthSetup.AddDiffPdfAuth"/>) over a throwaway SQLite database and maps
/// the token / authorization / userinfo / revocation endpoints plus one protected
/// route. Lets the OAuth flows be exercised without PostgreSQL or the full app.
/// </summary>
public sealed class AuthTestServer : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"diffpdf-auth-{Guid.NewGuid():N}.db");
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";
    public string RedirectUri => "http://localhost:7890/";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:ClientId"] = "diffpdf-ci",
            ["Auth:ClientSecret"] = "secret",
            ["Auth:Scope"] = "diffpdf.api",
            ["Auth:InteractiveClientId"] = "diffpdf-app",
            ["Auth:RedirectUris:0"] = RedirectUri,
            ["Auth:Users:0:Username"] = "tester",
            ["Auth:Users:0:Password"] = "pw",
            ["Auth:Users:0:Name"] = "QA Tester",
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>()!;
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
        builder.Services.AddDiffPdfAuth(o => o.UseSqlite($"Data Source={_dbPath}"), auth);

        var app = builder.Build();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTokenEndpoint(auth);
        app.MapInteractiveAuthEndpoints();
        app.MapGet("/protected", (ClaimsPrincipal user) =>
            Results.Ok(new { sub = user.FindFirstValue("sub") ?? user.Identity?.Name }));

        await app.StartAsync();
        BaseUrl = app.Urls.First();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* best effort */ }
    }
}
