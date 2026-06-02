using System.Reflection;
using DiffPdf.Api.Auth;
using DiffPdf.Core.Discovery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Discovery;

/// <summary>Builds the <see cref="DiffPdfServerDescriptor"/> advertised over UDP and HTTP.</summary>
public interface IServerDescriptorProvider
{
    /// <summary>Stable id of this server process.</summary>
    string InstanceId { get; }

    /// <summary>Descriptor for the given externally reachable base URL (e.g. the request host).</summary>
    DiffPdfServerDescriptor Create(string baseUrl);

    /// <summary>HTTP port the server listens on, parsed from its bound addresses (null if unknown).</summary>
    int? GetHttpPort();
}

/// <inheritdoc />
public sealed class ServerDescriptorProvider(
    IServer server,
    IOptions<DiscoveryOptions> discovery,
    IOptions<AuthOptions> auth) : IServerDescriptorProvider
{
    private static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");

    private static readonly string AssemblyVersion =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "1.0.0";

    private readonly DiscoveryOptions _discovery = discovery.Value;
    private readonly AuthOptions _auth = auth.Value;

    public string InstanceId => ProcessInstanceId;

    public DiffPdfServerDescriptor Create(string baseUrl) => new()
    {
        InstanceId = ProcessInstanceId,
        Name = string.IsNullOrWhiteSpace(_discovery.ServerName) ? Environment.MachineName : _discovery.ServerName,
        Version = AssemblyVersion,
        BaseUrl = baseUrl.TrimEnd('/'),
        AuthEnabled = _auth.Enabled,
        TokenEndpoint = _auth.Enabled ? "/connect/token" : null,
        GrantTypes = _auth.Enabled
            ? ["client_credentials", "authorization_code", "refresh_token"]
            : [],
    };

    public int? GetHttpPort()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null)
            return null;

        foreach (var address in addresses)
        {
            if (TryParsePort(address) is { } port)
                return port;
        }

        return null;
    }

    /// <summary>Extracts the port from a server binding such as <c>http://[::]:8080</c> or <c>http://+:5000</c>.</summary>
    private static int? TryParsePort(string address)
    {
        // Normalise wildcard hosts so Uri can parse them.
        string normalised = address
            .Replace("://+:", "://localhost:")
            .Replace("://[::]:", "://localhost:")
            .Replace("://0.0.0.0:", "://localhost:");

        return Uri.TryCreate(normalised, UriKind.Absolute, out var uri) && uri.Port > 0
            ? uri.Port
            : null;
    }
}
