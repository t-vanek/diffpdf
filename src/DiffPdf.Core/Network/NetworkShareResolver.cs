using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Network;

/// <summary>Raised when a request references a share or credential profile that is not configured.</summary>
public sealed class NetworkConfigurationException(string message) : Exception(message);

/// <summary>
/// A folder reference resolved against <see cref="NetworkOptions"/>: the concrete
/// path to read from plus the credentials (if any) needed to connect it.
/// </summary>
public sealed record ResolvedFolder(string Path, NetworkCredentials? Credentials, string? ShareName = null);

/// <summary>Public, secret-free view of a configured share.</summary>
public sealed record ShareInfo(string Name, string? Root, string? LocalMountPath, bool RequiresCredentials, string? Description);

/// <summary>
/// Expands share aliases and credential-profile references into a concrete path +
/// credentials, applying the policy in <see cref="NetworkOptions"/>.
/// </summary>
public interface INetworkShareResolver
{
    /// <summary>
    /// Resolves <paramref name="folder"/> (a local path, a UNC path, or a
    /// <c>share:&lt;name&gt;[/subpath]</c> alias) together with an optional inline
    /// credential set and/or a named credential profile.
    /// </summary>
    ResolvedFolder Resolve(string folder, NetworkCredentials? inlineCredentials = null, string? credentialProfile = null);

    /// <summary>Configured shares (names + locations only — never secrets).</summary>
    IReadOnlyList<ShareInfo> ListShares();

    /// <summary>Names of the configured credential profiles (never their secrets).</summary>
    IReadOnlyList<string> ListCredentialProfiles();
}

/// <inheritdoc />
public sealed class NetworkShareResolver(IOptions<NetworkOptions> options) : INetworkShareResolver
{
    private const string SharePrefix = "share:";

    private readonly NetworkOptions _options = options.Value;

    public ResolvedFolder Resolve(string folder, NetworkCredentials? inlineCredentials = null, string? credentialProfile = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new NetworkConfigurationException("Folder must not be empty.");

        string path = folder;
        string? shareName = null;

        if (TryParseShareAlias(folder, out string name, out string subPath))
        {
            if (!_options.Shares.TryGetValue(name, out var def))
                throw new NetworkConfigurationException(
                    $"Unknown share '{name}'. Configured shares: {DescribeKeys(_options.Shares.Keys)}.");

            shareName = name;
            path = CombineRoot(ShareRoot(name, def), subPath);

            // A share supplies a default credential profile when the caller did not pick one.
            credentialProfile ??= def.CredentialProfile;
        }

        var credentials = ResolveCredentials(inlineCredentials, credentialProfile);

        // Already-mounted / local paths are reached as-is; credentials are meaningless there.
        if (!UncPath.IsUnc(path))
            credentials = null;

        return new ResolvedFolder(path, credentials, shareName);
    }

    public IReadOnlyList<ShareInfo> ListShares() =>
        _options.Shares
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new ShareInfo(
                kvp.Key,
                kvp.Value.Root,
                kvp.Value.LocalMountPath,
                RequiresCredentials: kvp.Value.LocalMountPath is null && kvp.Value.CredentialProfile is not null,
                kvp.Value.Description))
            .ToList();

    public IReadOnlyList<string> ListCredentialProfiles() =>
        _options.CredentialProfiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    private NetworkCredentials? ResolveCredentials(NetworkCredentials? inline, string? profileName)
    {
        if (inline is not null)
        {
            if (!_options.AllowInlineCredentials)
                throw new NetworkConfigurationException(
                    "Inline credentials are disabled (Network:AllowInlineCredentials = false); use a configured credential profile.");
            return inline;
        }

        if (profileName is null)
            return null;

        if (!_options.CredentialProfiles.TryGetValue(profileName, out var profile))
            throw new NetworkConfigurationException(
                $"Unknown credential profile '{profileName}'. Configured profiles: {DescribeKeys(_options.CredentialProfiles.Keys)}.");

        return profile.ToCredentials();
    }

    private static string ShareRoot(string name, NetworkShareDefinition def)
    {
        string? root = def.LocalMountPath ?? def.Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new NetworkConfigurationException(
                $"Share '{name}' defines neither a Root (UNC) nor a LocalMountPath.");
        return root;
    }

    /// <summary>Parses <c>share:name/sub/path</c> (or <c>share://name/...</c>); returns false for any other path.</summary>
    private static bool TryParseShareAlias(string folder, out string name, out string subPath)
    {
        name = string.Empty;
        subPath = string.Empty;

        if (!folder.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string rest = folder[SharePrefix.Length..].TrimStart('/');
        if (rest.Length == 0)
            throw new NetworkConfigurationException("Share alias must include a name, e.g. 'share:reports/baseline'.");

        int slash = rest.IndexOf('/');
        if (slash < 0)
        {
            name = rest;
        }
        else
        {
            name = rest[..slash];
            subPath = rest[(slash + 1)..];
        }

        return true;
    }

    /// <summary>Joins a share root with a sub-path, keeping UNC separators for UNC roots.</summary>
    private static string CombineRoot(string root, string subPath)
    {
        if (string.IsNullOrEmpty(subPath))
            return root;

        if (UncPath.IsUnc(root))
        {
            string trimmed = root.TrimEnd('\\', '/');
            return $@"{trimmed}\{subPath.Replace('/', '\\')}";
        }

        return Path.Combine(root, subPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string DescribeKeys(IEnumerable<string> keys)
    {
        var list = keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? "(none configured)" : string.Join(", ", list);
    }
}
