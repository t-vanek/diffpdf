using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DiffPdf.Api.Auth;

/// <summary>
/// Interactive (authorization-code + PKCE) endpoints completing the OpenIddict
/// passthrough: a config-backed login, the authorization endpoint, end-session and
/// userinfo. The login is intentionally minimal — replace it with a real identity
/// provider in production.
/// </summary>
public static class InteractiveAuthEndpoints
{
    public static void MapInteractiveAuthEndpoints(this WebApplication app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext context) =>
        {
            var request = context.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            // Require an interactive login session; otherwise bounce to the login form.
            var login = await context.AuthenticateAsync(AuthSetup.LoginCookieScheme);
            if (login.Principal?.Identity is not { IsAuthenticated: true })
            {
                string returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl },
                    [AuthSetup.LoginCookieScheme]);
            }

            string subject = login.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? login.Principal.Identity!.Name!;

            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
            identity.AddClaim(Claims.Subject, subject);
            identity.AddClaim(Claims.Name, login.Principal.FindFirstValue(ClaimTypes.Name) ?? subject);

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());
            principal.SetDestinations(AuthSetup.GetDestinations);

            // Issues the authorization code (and redirects back to the client).
            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/account/login", (string? returnUrl, string? error) =>
            Results.Content(LoginPage(returnUrl, error), "text/html"))
            .AllowAnonymous().ExcludeFromDescription();

        app.MapPost("/account/login", async (HttpContext context, IOptions<AuthOptions> options) =>
        {
            var form = await context.Request.ReadFormAsync();
            string username = form["username"].ToString();
            string password = form["password"].ToString();
            string? returnUrl = form["returnUrl"].ToString();

            var user = options.Value.Users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.Ordinal) && u.Password == password);

            if (user is null)
                return Results.Content(LoginPage(returnUrl, "Invalid username or password."), "text/html");

            var identity = new ClaimsIdentity(AuthSetup.LoginCookieScheme);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Username));
            identity.AddClaim(new Claim(ClaimTypes.Name, user.Name ?? user.Username));

            await context.SignInAsync(AuthSetup.LoginCookieScheme, new ClaimsPrincipal(identity));

            // Only ever redirect to a local URL (open-redirect guard).
            string target = !string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                ? returnUrl
                : "/";
            return Results.Redirect(target);
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapMethods("/connect/logout", ["GET", "POST"], async (HttpContext context) =>
        {
            await context.SignOutAsync(AuthSetup.LoginCookieScheme);
            // OpenIddict completes the end-session response (post_logout redirect, etc.).
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }).AllowAnonymous().ExcludeFromDescription();

        // Requires a valid bearer token (fallback policy). Returns the subject claims.
        app.MapMethods("/connect/userinfo", ["GET", "POST"], (HttpContext context) =>
        {
            var user = context.User;
            var claims = new Dictionary<string, object?>
            {
                [Claims.Subject] = user.FindFirstValue(Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
            };

            string? name = user.FindFirstValue(Claims.Name) ?? user.FindFirstValue(ClaimTypes.Name);
            if (!string.IsNullOrEmpty(name))
                claims[Claims.Name] = name;

            return Results.Ok(claims);
        }).WithTags("Auth").WithSummary("OpenID Connect userinfo");
    }

    private static string LoginPage(string? returnUrl, string? error)
    {
        string err = string.IsNullOrEmpty(error)
            ? string.Empty
            : $"<p style=\"color:#b00\">{WebUtility.HtmlEncode(error)}</p>";
        string encodedReturn = WebUtility.HtmlEncode(returnUrl ?? string.Empty);

        return $$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>diffpdf — sign in</title></head>
            <body style="font-family:system-ui;max-width:22rem;margin:4rem auto">
              <h1>diffpdf</h1>
              {{err}}
              <form method="post" action="/account/login">
                <input type="hidden" name="returnUrl" value="{{encodedReturn}}" />
                <p><label>User<br><input name="username" autofocus style="width:100%"></label></p>
                <p><label>Password<br><input name="password" type="password" style="width:100%"></label></p>
                <p><button type="submit">Sign in</button></p>
              </form>
            </body>
            </html>
            """;
    }
}
