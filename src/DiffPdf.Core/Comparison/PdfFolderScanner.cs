namespace DiffPdf.Core.Comparison;

/// <summary>
/// Enumerates the files under a folder: a complete count plus their relative
/// ('/'-normalised) paths, optionally capped. Shared by folder discovery and
/// instance-structure inspection so both count/list PDFs the same way.
/// </summary>
public static class PdfFolderScanner
{
    /// <summary>
    /// Counts files matching <paramref name="searchPattern"/> under <paramref name="folder"/>
    /// and collects their relative paths up to <paramref name="sampleLimit"/>.
    /// </summary>
    /// <param name="sampleLimit">Max relative paths to collect: <c>null</c> = all, <c>0</c> = none, <c>N</c> = first N. The returned count is always complete.</param>
    /// <returns>The total match count and the (possibly capped, never null) list of relative paths.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public static (int Count, IReadOnlyList<string> Files) Scan(
        string folder, string searchPattern, bool recursive, int? sampleLimit, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = new List<string>();
        int count = 0;

        foreach (var file in Directory.EnumerateFiles(folder, searchPattern, option))
        {
            ct.ThrowIfCancellationRequested();
            count++;
            if (sampleLimit is null || files.Count < sampleLimit)
                files.Add(Path.GetRelativePath(folder, file).Replace('\\', '/'));
        }

        return (count, files);
    }
}
