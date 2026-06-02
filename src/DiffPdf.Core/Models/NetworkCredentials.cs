namespace DiffPdf.Core.Models;

/// <summary>
/// Credentials used to access a network share. Supplied per request for an
/// old/new folder that lives on an authenticated SMB/CIFS share.
/// </summary>
public sealed record NetworkCredentials
{
    public required string Username { get; init; }
    public required string Password { get; init; }

    /// <summary>Optional Windows/AD domain.</summary>
    public string? Domain { get; init; }

    /// <summary>"DOMAIN\\user" when a domain is set, otherwise just the username.</summary>
    public string QualifiedUsername =>
        string.IsNullOrEmpty(Domain) ? Username : $"{Domain}\\{Username}";
}
