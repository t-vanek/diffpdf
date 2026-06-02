namespace DiffPdf.Core.Comparison;

/// <summary>A matched (or unmatched) file pair: paths are null when the file is missing on that side.</summary>
public sealed record FilePair(string RelativePath, string? OldPath, string? NewPath);

/// <summary>Pairs files in two folders by relative path.</summary>
public static class FolderPairing
{
    public static IReadOnlyList<FilePair> Pair(string oldRoot, string newRoot, string searchPattern, bool recursive)
    {
        var oldFiles = Index(oldRoot, searchPattern, recursive);
        var newFiles = Index(newRoot, searchPattern, recursive);

        return oldFiles.Keys.Union(newFiles.Keys)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(key => new FilePair(
                key,
                oldFiles.GetValueOrDefault(key),
                newFiles.GetValueOrDefault(key)))
            .ToList();
    }

    private static Dictionary<string, string> Index(string root, string pattern, bool recursive)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Folder not found: {root}");

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(root, pattern, option)
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => f,
                StringComparer.OrdinalIgnoreCase);
    }
}
