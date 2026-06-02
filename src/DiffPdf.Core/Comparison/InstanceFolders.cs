namespace DiffPdf.Core.Comparison;

/// <summary>
/// Derives an instance's conventional <c>old/</c>, <c>new/</c> and <c>reports/</c>
/// subfolders from its (already resolved) base path, preserving UNC separators.
/// </summary>
public static class InstanceFolders
{
    public const string OldName = "old";
    public const string NewName = "new";
    public const string ReportsName = "reports";

    public static string Old(string basePath) => Combine(basePath, OldName);
    public static string New(string basePath) => Combine(basePath, NewName);
    public static string Reports(string basePath) => Combine(basePath, ReportsName);

    /// <summary>Joins a base folder with a subfolder, keeping UNC separators for UNC roots.</summary>
    public static string Combine(string root, string sub) =>
        UncPath.IsUnc(root) ? $@"{root.TrimEnd('\\', '/')}\{sub}" : Path.Combine(root, sub);
}
