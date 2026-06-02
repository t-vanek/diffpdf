using DiffPdf.Core.Models;

namespace DiffPdf.Core.Network;

/// <summary>
/// Central, configuration-driven network access. Bound from the <c>"Network"</c>
/// section so credentials and shares are defined once (in config / a secret store)
/// instead of being embedded in every comparison request.
/// </summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>
    /// Named credential profiles. A request (or a share) references one by name, so
    /// raw passwords live in configuration and never have to be sent — or persisted —
    /// per request.
    /// </summary>
    public Dictionary<string, NetworkCredentialProfile> CredentialProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Named shares. A request can target one with the <c>share:&lt;name&gt;[/subpath]</c>
    /// scheme instead of spelling out a UNC path and credentials.
    /// </summary>
    public Dictionary<string, NetworkShareDefinition> Shares { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When false, requests may not carry inline credentials — only configured
    /// credential profiles are accepted. Lets an operator forbid passwords in
    /// request bodies entirely.
    /// </summary>
    public bool AllowInlineCredentials { get; set; } = true;

    /// <summary>Mount authenticated CIFS shares read-only (Linux). Defaults to true.</summary>
    public bool MountReadOnly { get; set; } = true;

    /// <summary>
    /// Extra comma-separated options appended to the Linux <c>mount.cifs -o</c> string
    /// (e.g. <c>vers=3.0,sec=ntlmssp</c>). Ignored on Windows.
    /// </summary>
    public string? CifsMountOptions { get; set; }
}

/// <summary>A reusable set of credentials, referenced by name from requests and shares.</summary>
public sealed class NetworkCredentialProfile
{
    public required string Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Optional Windows/AD domain.</summary>
    public string? Domain { get; set; }

    public NetworkCredentials ToCredentials() => new()
    {
        Username = Username,
        Password = Password ?? string.Empty,
        Domain = Domain,
    };
}

/// <summary>
/// A named share. Either a UNC <see cref="Root"/> (connected with the share's
/// credential profile) or an already-mounted <see cref="LocalMountPath"/>. Prefer
/// <see cref="LocalMountPath"/> for the durable pipeline: every worker reaches the
/// same path and no runtime mount or persisted credentials are needed.
/// </summary>
public sealed class NetworkShareDefinition
{
    /// <summary>UNC root of the share, e.g. <c>\\fileserver\reports</c>.</summary>
    public string? Root { get; set; }

    /// <summary>Already-mounted local path that exposes this share to every worker.</summary>
    public string? LocalMountPath { get; set; }

    /// <summary>Credential profile used when the share is reached by its UNC <see cref="Root"/>.</summary>
    public string? CredentialProfile { get; set; }

    /// <summary>Optional human-readable description, surfaced by discovery.</summary>
    public string? Description { get; set; }
}
