using System.Net;
using DiffPdf.Client;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DiffPdf.Client.Tests;

/// <summary>
/// Drives the SDK against a live in-memory instance of the API (WebApplicationFactory,
/// in-memory store fallback, no DB / Ghostscript needed). Exercises the management flow
/// end-to-end so the SDK's models, routes and (de)serialization stay in sync with the API.
/// </summary>
public class DiffPdfClientIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private DiffPdfClient NewClient() => new(factory.CreateClient());

    [Fact]
    public async Task ManagementFlow_Branch_Instance_Structure_Readiness_Batch_Cancel()
    {
        var diff = NewClient();
        Assert.True(await diff.HealthAsync());

        string bk = "B" + Guid.NewGuid().ToString("N")[..8];
        string ik = "I" + Guid.NewGuid().ToString("N")[..8];

        // 1) branch
        var branch = await diff.CreateBranchAsync(new(bk, "Branch"));
        Assert.Equal(bk, branch.Key);

        // 2) instance with a fresh temp base — auto-ensure creates old/new/reports
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var created = await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));
            Assert.Equal(ik, created.Instance.Key);
            Assert.NotNull(created.Structure);
            Assert.True(created.Structure!.Ok);

            // 3) readiness now carries the structure inspection too: old/new exist but empty,
            //    so it is not ready (nothing to compare)
            var readiness = await diff.GetReadinessAsync(bk, ik, includeFiles: true);
            Assert.Equal(0, readiness.Structure.Items.Single(i => i.Name == "old").PdfCount);
            Assert.False(readiness.Ready);

            // 5) create a Draft job
            var job = await diff.CreateBatchAsync(new SubmitBatchRequest { Scope = new(bk, ik) });
            Assert.Equal(JobStatus.Draft, job.Status);

            // 6) starting an empty instance -> 422 from the gate
            var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.StartJobAsync(job.Id));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);

            // 7) cancel the Draft
            var cancelled = await diff.CancelJobAsync(job.Id);
            Assert.Equal(JobStatus.Cancelled, cancelled.Status);

            // 8) it shows up when listing by scope
            var jobs = await diff.ListJobsAsync(branchKey: bk);
            Assert.Contains(jobs, j => j.Id == job.Id);
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task GetUnknown_ReturnsNull_NotThrow()
    {
        var diff = NewClient();
        Assert.Null(await diff.GetBranchAsync("does-not-exist"));
        Assert.Null(await diff.GetJobAsync(Guid.NewGuid()));
    }
}
