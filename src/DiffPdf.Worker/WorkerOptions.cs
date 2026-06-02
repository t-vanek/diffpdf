namespace DiffPdf.Worker;

public sealed class WorkerOptions
{
    /// <summary>Root directory where per-job diff artifacts (highlighted PDFs) are written.</summary>
    public string ArtifactRoot { get; set; } =
        Environment.GetEnvironmentVariable("DIFFPDF_ARTIFACT_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "diffpdf-artifacts");
}
