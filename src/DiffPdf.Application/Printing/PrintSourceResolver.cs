using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Network;
using Microsoft.Extensions.Options;

namespace DiffPdf.Application.Printing;

/// <summary>
/// Resolves a scope's <see cref="PrintSoaConnection"/> from <see cref="TiskOptions"/>, taking the SOA
/// login/password from the referenced <see cref="NetworkOptions"/> credential profile so passwords stay
/// in configuration. The SOA client id (the "zakázka" name) and endpoint come from the print source itself.
/// </summary>
public sealed class PrintSourceResolver(IOptions<TiskOptions> tisk, IOptions<NetworkOptions> network) : IPrintSourceResolver
{
    private readonly TiskOptions _tisk = tisk.Value;
    private readonly NetworkOptions _network = network.Value;

    public bool IsConfigured(string branchKey, string instanceKey) =>
        _tisk.Sources.ContainsKey(TiskOptions.ScopeKey(branchKey, instanceKey));

    public IReadOnlyCollection<string> ConfiguredScopes => _tisk.Sources.Keys;

    public PrintSoaConnection Resolve(string branchKey, string instanceKey)
    {
        var key = TiskOptions.ScopeKey(branchKey, instanceKey);
        if (!_tisk.Sources.TryGetValue(key, out var src))
            throw new InvalidOperationException($"No print source is configured for scope '{key}'.");

        // The SOA login is a service user; it must come from a credential profile (never inline).
        if (string.IsNullOrWhiteSpace(src.CredentialProfile))
            throw new InvalidOperationException(
                $"Print source '{key}' has no CredentialProfile; a profile supplying the SOA login is required.");

        if (!_network.CredentialProfiles.TryGetValue(src.CredentialProfile, out var profile))
            throw new InvalidOperationException(
                $"Print source '{key}' references unknown credential profile '{src.CredentialProfile}'.");

        return new PrintSoaConnection(
            SoaManagerUri: src.SoaManagerUri,
            SoaClientId: src.SoaClientId,
            Login: profile.Username,
            Password: profile.Password ?? string.Empty,
            TextPattern: src.TextPattern);
    }
}
