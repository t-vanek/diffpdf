using System.Text.RegularExpressions;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Client-side mirror of the server's <c>DiffPdf.Core.Storage.StorageKeyValidator</c>: a branch/instance key
/// becomes a filesystem path segment and a URL segment, so it must stay on a safe whitelist. Kept in sync with
/// the server (the SDK deliberately does not reference Core), purely for fast in-form feedback — the server
/// still validates authoritatively.
/// </summary>
internal static partial class ScopeKey
{
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,64}$")]
    private static partial Regex Allowed();

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("..", StringComparison.Ordinal)
        && Allowed().IsMatch(value);

    /// <summary>Human-readable rule shown when a key is rejected.</summary>
    public const string Rule = "Klíč smí obsahovat jen písmena, číslice, '_', '.', '-' (1–64 znaků) a nesmí obsahovat '..'.";
}
