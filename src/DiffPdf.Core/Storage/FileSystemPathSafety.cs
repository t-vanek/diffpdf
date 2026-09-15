namespace DiffPdf.Core.Storage;

/// <summary>
/// Rejects existing links before file-manager operations, including links above the configured root.
/// Filesystem permissions must also prevent untrusted writers from replacing ancestors during an operation.
/// </summary>
public static class FileSystemPathSafety
{
    public static bool ContainsLink(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
            ancestors.Push(current);

        // Check parents first so even metadata below a known link is never queried.
        foreach (string current in ancestors)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { /* A new destination can be absent; still check its parents. */ }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }
}
