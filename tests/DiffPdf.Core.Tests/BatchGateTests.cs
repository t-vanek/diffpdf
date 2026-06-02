using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class BatchGateTests
{
    private static BatchComparisonReport Report(BatchGate? gate, params FilePairResult[] files) => new()
    {
        OldFolder = "old",
        NewFolder = "new",
        Files = files,
        Gate = gate,
    };

    private static FilePairResult File(FilePairStatus status, int contentErrors = 0) =>
        new() { RelativePath = Guid.NewGuid().ToString(), Status = status, ContentErrorCount = contentErrors };

    [Fact]
    public void NoGate_AlwaysPasses()
    {
        var report = Report(null, File(FilePairStatus.Differs), File(FilePairStatus.Error));
        Assert.True(report.Passed);
        Assert.Empty(report.GateViolations);
    }

    [Fact]
    public void FailOnAnyDifference_FailsWhenAnyFileDiffers()
    {
        var report = Report(new BatchGate { FailOnAnyDifference = true }, File(FilePairStatus.Identical), File(FilePairStatus.Differs));
        Assert.False(report.Passed);
        Assert.Single(report.GateViolations);
    }

    [Fact]
    public void Limits_PassWhenWithinThresholds()
    {
        var gate = new BatchGate { MaxDifferingFiles = 2, MaxErrors = 0, MaxFilesWithContentErrors = 0 };
        var report = Report(gate, File(FilePairStatus.Differs), File(FilePairStatus.Identical));
        Assert.True(report.Passed);
    }

    [Fact]
    public void Limits_FailOnErrorsAndContentErrors()
    {
        var gate = new BatchGate { MaxErrors = 0, MaxFilesWithContentErrors = 0 };
        var report = Report(gate, File(FilePairStatus.Error), File(FilePairStatus.Differs, contentErrors: 3));
        Assert.False(report.Passed);
        Assert.Equal(2, report.GateViolations.Count);
    }
}
