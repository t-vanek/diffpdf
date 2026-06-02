namespace DiffPdf.Core.Comparison;

/// <summary>Helpers for recognizing and splitting UNC network paths.</summary>
public static class UncPath
{
    /// <summary>True for <c>\\server\share\...</c> or <c>//server/share/...</c>.</summary>
    public static bool IsUnc(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>
    /// Splits a UNC path into server, share and the remaining sub-path
    /// (forward-slash separated). Throws for non-UNC input.
    /// </summary>
    public static (string Server, string Share, string SubPath) Split(string uncPath)
    {
        if (!IsUnc(uncPath))
            throw new ArgumentException($"Not a UNC path: {uncPath}", nameof(uncPath));

        var segments = uncPath
            .Replace('\\', '/')
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
            throw new ArgumentException($"UNC path must include server and share: {uncPath}", nameof(uncPath));

        string server = segments[0];
        string share = segments[1];
        string subPath = string.Join('/', segments.Skip(2));
        return (server, share, subPath);
    }
}
