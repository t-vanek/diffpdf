using System.Collections.Concurrent;
using D3Soft.Auth.Authentication.Proxy;
using D3Soft.Auth.Authentication.Shared.BusinessIntegrity; // TokenRequestOptions
using D3Soft.Auth.Authentication.Shared.DataContracts;     // TokenRequestBasicDC
using DiffPdf.Application.Abstractions;                     // PrintSoaConnection

namespace DiffPdf.Tisk;

/// <summary>
/// Builds and caches authenticated <see cref="D3JwtWcfProxyScope"/>s for the D3Soft SOA.
/// Ported verbatim from HromadneVzory's <c>WcfContextProvider</c>: one JWT scope per
/// (uri, client, user, password) tuple, created once and reused. All proxy calls must run
/// inside <c>scope.RunIsolated(...)</c> because the generated WCF proxies are thread-affine.
/// </summary>
internal static class WcfProxyScopeProvider
{
    private static readonly ConcurrentDictionary<string, D3JwtWcfProxyScope> Scopes = new();

    public static D3JwtWcfProxyScope GetOrCreate(PrintSoaConnection conn)
    {
        var cacheKey = $"{conn.SoaManagerUri}|{conn.SoaClientId}|{conn.Login}|{conn.Password}";
        return Scopes.GetOrAdd(cacheKey, _ => Create(conn));
    }

    private static D3JwtWcfProxyScope Create(PrintSoaConnection conn)
    {
        var tokenRequest = new TokenRequestBasicDC
        {
            SoaManagerUri = conn.SoaManagerUri,
            SoaClientId = conn.SoaClientId,
            UserName = conn.Login,
            Password = conn.Password,
            Options = TokenRequestOptions.RequestRefreshToken,
        };
        return new D3JwtWcfProxyScope(tokenRequest);
    }
}
