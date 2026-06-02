namespace DiffPdf.Core.Storage;

public sealed class StorageOptions
{
    /// <summary>Root directory under which all per-instance / per-project / per-job folders live.</summary>
    public string RootPath { get; set; } =
        Environment.GetEnvironmentVariable("DIFFPDF_STORAGE_ROOT") ?? "storage";

    public bool CreateFoldersOnDemand { get; set; } = true;
}
