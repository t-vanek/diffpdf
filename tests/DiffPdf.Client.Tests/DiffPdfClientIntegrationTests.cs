using System.Net;
using DiffPdf.Client;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DiffPdf.Client.Tests;

/// <summary>
/// Drives the SDK against a live in-memory instance of the API (WebApplicationFactory,
/// in-memory store + in-process Wolverine fallback, no DB / Ghostscript needed). Exercises the
/// automation flow end-to-end so the SDK's models, routes and (de)serialization stay in sync
/// with the API.
/// </summary>
public class DiffPdfClientIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private DiffPdfClient NewClient() => new(factory.CreateClient());

    private static (string bk, string ik) FreshKeys() =>
        ("B" + Guid.NewGuid().ToString("N")[..8], "I" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Schedule_Crud_RoundTrip()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "Branch"));

        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));

            // create
            var created = await diff.CreateScheduleAsync(bk, ik, new CreateScheduleRequest
            {
                Key = "nightly",
                Name = "Nightly",
                Cron = "0 2 * * *",
            });
            Assert.Equal("nightly", created.Key);
            Assert.Equal(1, created.Version);

            // get + list
            var got = await diff.GetScheduleAsync(bk, ik, "nightly");
            Assert.NotNull(got);
            Assert.Equal("0 2 * * *", got!.Cron);
            Assert.Contains(await diff.ListSchedulesAsync(bk, ik), s => s.Key == "nightly");

            // update (optimistic concurrency via Version)
            var updated = await diff.UpdateScheduleAsync(bk, ik, "nightly", new UpdateScheduleRequest
            {
                Cron = "0 3 * * *",
                Enabled = false,
                Version = created.Version,
            });
            Assert.Equal("0 3 * * *", updated.Cron);
            Assert.False(updated.Enabled);
            Assert.Equal(2, updated.Version);

            // duplicate key -> 409
            var dup = await Assert.ThrowsAsync<DiffPdfApiException>(() =>
                diff.CreateScheduleAsync(bk, ik, new CreateScheduleRequest { Key = "nightly", Cron = "0 2 * * *" }));
            Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

            // invalid cron -> 400
            var bad = await Assert.ThrowsAsync<DiffPdfApiException>(() =>
                diff.CreateScheduleAsync(bk, ik, new CreateScheduleRequest { Key = "broken", Cron = "not-a-cron" }));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            // delete
            await diff.DeleteScheduleAsync(bk, ik, "nightly");
            Assert.Null(await diff.GetScheduleAsync(bk, ik, "nightly"));
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RunNow_EmptyInstance_Returns422()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "Branch"));

        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));      // old/new auto-created, empty
            await diff.CreateScheduleAsync(bk, ik, new CreateScheduleRequest { Key = "s", Cron = "0 2 * * *" });

            // Nothing to compare -> the same gate the removed POST /jobs/{id}/start enforced.
            var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.RunScheduleNowAsync(bk, ik, "s"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RunNow_HappyPath_JobReachesCompleted()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "Branch"));

        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));

            // One matching pair so the pre-flight gate passes and the pipeline has work. The files are
            // intentional stubs (not real PDFs): the engine records each as an Error without crashing the
            // batch, so the job still runs RunBatch -> Index -> ComparePair -> Finalize -> Completed. This
            // proves the automation wiring end-to-end while staying hermetic (no Ghostscript / real PDFs).
            await File.WriteAllTextAsync(Path.Combine(basePath, "old", "doc.pdf"), "%PDF-1.4 stub");
            await File.WriteAllTextAsync(Path.Combine(basePath, "new", "doc.pdf"), "%PDF-1.4 stub");

            await diff.CreateScheduleAsync(bk, ik, new CreateScheduleRequest { Key = "s", Cron = "0 2 * * *" });

            Guid jobId = await diff.RunScheduleNowAsync(bk, ik, "s");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var report = await diff.WaitForReportAsync(jobId, TimeSpan.FromMilliseconds(200), cts.Token);

            Assert.Equal(1, report.Total);   // the pipeline indexed and processed the single pair
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Subscription_Crud_RoundTrip()
    {
        var diff = NewClient();

        var created = await diff.CreateSubscriptionAsync(new CreateSubscriptionRequest
        {
            Channel = "webhook",
            Target = "https://hooks.example/test",
            Events = [NotificationEvent.GateViolated, NotificationEvent.Completed],
        });
        Assert.Equal("webhook", created.Channel);
        Assert.Equal(1, created.Version);

        var got = await diff.GetSubscriptionAsync(created.Id);
        Assert.NotNull(got);
        Assert.Contains(await diff.ListSubscriptionsAsync(), s => s.Id == created.Id);

        var updated = await diff.UpdateSubscriptionAsync(created.Id, new UpdateSubscriptionRequest
        {
            Channel = "webhook",
            Target = "https://hooks.example/changed",
            Events = [NotificationEvent.GateViolated],
            Enabled = false,
            Version = created.Version,
        });
        Assert.Equal("https://hooks.example/changed", updated.Target);
        Assert.Equal(2, updated.Version);

        // bad channel -> 400
        var bad = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.CreateSubscriptionAsync(
            new CreateSubscriptionRequest { Channel = "carrier-pigeon", Target = "x", Events = [NotificationEvent.Completed] }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        await diff.DeleteSubscriptionAsync(created.Id);
        Assert.Null(await diff.GetSubscriptionAsync(created.Id));
    }

    [Fact]
    public async Task GetUnknown_ReturnsNull_NotThrow()
    {
        var diff = NewClient();
        Assert.Null(await diff.GetBranchAsync("does-not-exist"));
        Assert.Null(await diff.GetJobAsync(Guid.NewGuid()));
    }
}
