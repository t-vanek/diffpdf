using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace DiffPdf.Core.Storage;

/// <summary>
/// Validates and resolves client-supplied virtual paths — '/'-separated, relative to a configured
/// root — to absolute filesystem paths guaranteed to stay inside that root. The companion of
/// <see cref="StorageKeyValidator"/> for whole relative paths: display names need spaces/diacritics,
/// so instead of a key whitelist each segment is checked against Windows filename rules (invalid
/// chars, reserved device names, trailing dot/space, "."/".."), and the final absolute path is
/// canonicalised and prefix-checked as defence in depth. Purely lexical — never touches the disk.
/// </summary>
public static class VirtualPath
{
    public const int MaxNameLength = 255;
    public const int MaxSegments = 64;

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    // Windows reserves these device names with ANY extension ("CON.pdf" is as unusable as "CON").
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when <paramref name="name"/> is usable as a single file/folder name (no separators).</summary>
    public static bool IsValidName([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        if (name is "." or "..") return false;
        if (name.AsSpan().IndexOfAny(InvalidNameChars) >= 0) return false;
        // Windows silently strips trailing dots/spaces (a created "x." becomes "x"), and a leading
        // space is legal but invisible in every UI — reject both rather than store a surprise.
        if (name[0] == ' ' || name[^1] is '.' or ' ') return false;

        int firstDot = name.IndexOf('.');
        string baseName = (firstDot < 0 ? name : name[..firstDot]).TrimEnd(' ');
        return !ReservedNames.Contains(baseName);
    }

    /// <summary>
    /// Normalizes a virtual path to canonical form: segments joined with '/', no leading/trailing
    /// separators ("" = the root itself). Accepts '\' as a separator too, collapses duplicates.
    /// False for rooted paths (<c>C:\x</c>, <c>\\unc</c>, <c>/x</c>), traversal ("..", "."), or any
    /// segment that is not a valid name.
    /// </summary>
    public static bool TryNormalize(string? virtualPath, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        string trimmed = virtualPath?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            normalized = string.Empty;
            return true;
        }
        if (Path.IsPathRooted(trimmed)) return false;

        string[] segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > MaxSegments) return false;
        foreach (string segment in segments)
            if (!IsValidName(segment)) return false;

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// Resolves a virtual path against <paramref name="rootPath"/> (local or UNC). On success,
    /// <paramref name="absolutePath"/> is canonical (<see cref="Path.GetFullPath(string)"/>) and verified
    /// to lie inside the root, and <paramref name="normalized"/> is the canonical virtual form.
    /// </summary>
    public static bool TryResolve(
        string rootPath, string? virtualPath,
        [NotNullWhen(true)] out string? absolutePath, [NotNullWhen(true)] out string? normalized)
    {
        absolutePath = null;
        if (!TryNormalize(virtualPath, out normalized)) return false;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            normalized = null;
            return false;
        }

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            candidate = normalized.Length == 0
                ? rootFull
                : Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            normalized = null;
            return false;
        }

        // Defence in depth: segments are already whitelisted, but verify the canonical result anyway.
        if (candidate.Length > rootFull.Length
            && !candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            normalized = null;
            return false;
        }
        if (candidate.Length <= rootFull.Length && !string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            normalized = null;
            return false;
        }

        absolutePath = candidate;
        return true;
    }

    /// <summary>Parent of a normalized virtual path ("" for a top-level item; "" stays "").</summary>
    public static string GetParent(string normalized)
    {
        int lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }

    /// <summary>Joins a normalized directory path and a name ("" directory = top level).</summary>
    public static string Combine(string directory, string name) =>
        directory.Length == 0 ? name : $"{directory}/{name}";
}
