using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Core.Tests;

public class StoragePathProviderTests
{
    private static ComparisonJob JobWithReports(string reportsFolder) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "LamaEnergy"),
            OldFolder = "/base/old",
            NewFolder = "/base/new",
            ReportsFolder = reportsFolder,
        },
    };

    [Fact]
    public void JobRoot_Is_ReportsFolder_Plus_JobId()
    {
        var provider = new FileSystemStoragePathProvider();
        var job = JobWithReports("/base/reports");

        string expectedRoot = Path.Combine("/base/reports", job.Id.ToString("N"));

        Assert.Equal(expectedRoot, provider.GetJobRoot(job));
        Assert.Equal(Path.Combine(expectedRoot, "artifacts"), provider.GetArtifactsPath(job));
        Assert.Equal(expectedRoot, provider.GetReportsPath(job));
        Assert.Equal(Path.Combine(expectedRoot, "logs"), provider.GetLogsPath(job));
    }

    [Fact]
    public void MissingReportsFolder_Throws()
    {
        var provider = new FileSystemStoragePathProvider();
        var job = JobWithReports("");

        Assert.Throws<InvalidOperationException>(() => provider.GetJobRoot(job));
    }
}
