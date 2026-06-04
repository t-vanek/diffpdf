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
public class DiffPdfClientIntegrationTests(InMemoryApiFactory factory)
    : IClassFixture<InMemoryApiFactory>
{
    private DiffPdfClient NewClient() => new(factory.CreateClient());

    private static (string bk, string ik) FreshKeys() =>
        ("B" + Guid.NewGuid().ToString("N")[..8], "I" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Check_Crud_Run_History_RoundTrip()
    {
        var diff = NewClient();

        // create (global health check on an interval)
        var created = await diff.CreateCheckAsync(new CreateCheckRequest
        {
            Key = "chk_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Server health",
            Type = CheckType.Health,
            ScopeKind = CheckScopeKind.Global,
            IntervalSeconds = 300,
            Events = [NotificationEvent.HealthDegraded],
        });
        Assert.Equal(CheckType.Health, created.Type);
        Assert.Equal(1, created.Version);

        // get + list
        var got = await diff.GetCheckAsync(created.Id);
        Assert.NotNull(got);
        Assert.Contains(await diff.ListChecksAsync(), c => c.Id == created.Id);

        // update (optimistic concurrency via Version)
        var updated = await diff.UpdateCheckAsync(created.Id, new UpdateCheckRequest
        {
            Key = created.Key, Name = "Renamed", Type = CheckType.Health,
            IntervalSeconds = 600, Enabled = false, Version = created.Version,
        });
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(2, updated.Version);

        // duplicate key -> 409
        var dup = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.CreateCheckAsync(
            new CreateCheckRequest { Key = created.Key, Type = CheckType.Health, IntervalSeconds = 60 }));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // run now -> recorded run + history
        var run = await diff.RunCheckAsync(created.Id);
        Assert.Equal(created.Id, run.CheckId);
        Assert.True((await diff.ListCheckRunsAsync(created.Id)).Count >= 1);

        // delete
        await diff.DeleteCheckAsync(created.Id);
        Assert.Null(await diff.GetCheckAsync(created.Id));
    }

    [Fact]
    public async Task Trigger_HappyPath_JobReachesCompleted()
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
            // proves the trigger wiring end-to-end while staying hermetic (no Ghostscript / real PDFs).
            await File.WriteAllTextAsync(Path.Combine(basePath, "old", "doc.pdf"), "%PDF-1.4 stub");
            await File.WriteAllTextAsync(Path.Combine(basePath, "new", "doc.pdf"), "%PDF-1.4 stub");

            var trig = await diff.TriggerBatchAsync(bk, ik);
            Assert.Equal("Launched", trig.Outcome);
            Assert.NotNull(trig.JobId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var report = await diff.WaitForReportAsync(trig.JobId!.Value, TimeSpan.FromMilliseconds(200), cts.Token);

            Assert.Equal(1, report.Total);   // the pipeline indexed and processed the single pair

            // Per-instance stats reflect the finished job and its file-pair comparison.
            var stats = await diff.GetInstanceStatsAsync(bk, ik);
            Assert.True(stats.Jobs.Completed >= 1);
            Assert.True(stats.Comparisons.Completed + stats.Comparisons.Failed + stats.Comparisons.Skipped >= 1);
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

    [Fact]
    public async Task Triggers_RespectReadinessGate_AndFanOut()
    {
        var diff = NewClient();
        string bk = "B" + Guid.NewGuid().ToString("N")[..8];
        string ik = "I" + Guid.NewGuid().ToString("N")[..8];

        await diff.CreateBranchAsync(new(bk, "Branch"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-trig-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath)); // auto-ensures empty old/new/reports

            // Unknown instance under an existing branch -> 404.
            var notFound = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.TriggerBatchAsync(bk, "nope"));
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

            // Empty instance -> accepted but nothing launched.
            var trig = await diff.TriggerBatchAsync(bk, ik);
            Assert.Equal("NothingToCompare", trig.Outcome);
            Assert.Null(trig.JobId);

            // Fan-out over the branch: the single empty instance is skipped.
            var run = await diff.RunBranchAsync(bk);
            Assert.Equal(bk, run.BranchKey);
            Assert.Equal(0, run.Launched);
            Assert.Equal(1, run.Skipped);
            Assert.Contains(run.Instances, i => i.InstanceKey == ik && i.Outcome == "NothingToCompare");
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OperationalVisibility_Health_Readiness_Status()
    {
        var diff = NewClient();

        // Liveness — always up while serving HTTP.
        Assert.True(await diff.HealthAsync());

        // Readiness returns the per-check breakdown for both 200 (ready) and 503 (degraded — e.g. when
        // Ghostscript isn't installed on this box). The database check is always OK on the in-memory store.
        var ready = await diff.GetReadinessAsync();
        Assert.Contains(ready.Status, new[] { "ready", "degraded" });
        var dbCheck = Assert.Single(ready.Checks, c => c.Name == "database");
        Assert.True(dbCheck.Ok);

        // Full status — the single in-process replica leads itself; the known services are listed.
        var status = await diff.GetStatusAsync();
        Assert.False(string.IsNullOrEmpty(status.Replica.WorkerInstanceId));
        Assert.False(string.IsNullOrEmpty(status.Replica.Version));
        Assert.True(status.Leader.IsThisReplica);
        Assert.Equal(status.Replica.WorkerInstanceId, status.Leader.Owner);
        Assert.True(status.Dependencies.Database.Ok);
        Assert.Contains(status.Services, s => s.Service == "control-plane");
        Assert.Contains(status.Services, s => s.Service == "stale-recovery");
    }

    [Fact]
    public async Task Branch_Delete_RespectsInstanceGuard()
    {
        var diff = NewClient();

        // Empty branch deletes cleanly (204) and is then gone.
        var (bk1, _) = FreshKeys();
        await diff.CreateBranchAsync(new(bk1, "Empty"));
        await diff.DeleteBranchAsync(bk1);
        Assert.Null(await diff.GetBranchAsync(bk1));

        // A branch that holds an instance cannot be deleted (409) and stays.
        var (bk2, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk2, "WithInstance"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-del-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk2, new(ik, "Inst", basePath));
            var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.DeleteBranchAsync(bk2));
            Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
            Assert.NotNull(await diff.GetBranchAsync(bk2));
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Instance_Delete_RespectsJobGuard()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "B"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-idel-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));

            // Empty instance deletes (204) and is then gone.
            await diff.DeleteInstanceAsync(bk, ik);
            Assert.Null(await diff.GetInstanceAsync(bk, ik));

            // Re-create + run a batch (via trigger) -> the instance now has job history and cannot be deleted (409).
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));
            await File.WriteAllTextAsync(Path.Combine(basePath, "old", "doc.pdf"), "%PDF-1.4 stub");
            await File.WriteAllTextAsync(Path.Combine(basePath, "new", "doc.pdf"), "%PDF-1.4 stub");
            var trig = await diff.TriggerBatchAsync(bk, ik);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await diff.WaitForReportAsync(trig.JobId!.Value, TimeSpan.FromMilliseconds(200), cts.Token);

            var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => diff.DeleteInstanceAsync(bk, ik));
            Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
            Assert.NotNull(await diff.GetInstanceAsync(bk, ik));
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Branch_Update_RoundTrip_AndVersionConflict()
    {
        var diff = NewClient();
        var (bk, _) = FreshKeys();
        var created = await diff.CreateBranchAsync(new(bk, "Orig"));

        var updated = await diff.UpdateBranchAsync(bk, new UpdateBranchRequest("Renamed", Enabled: false, created.Version));
        Assert.Equal("Renamed", updated.Name);
        Assert.False(updated.Enabled);
        Assert.Equal(created.Version + 1, updated.Version);
        Assert.Equal("Renamed", (await diff.GetBranchAsync(bk))!.Name);

        // Stale version -> 409.
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => diff.UpdateBranchAsync(bk, new UpdateBranchRequest("X", Enabled: true, created.Version)));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
    }

    [Fact]
    public async Task Instance_Update_RoundTrip()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "B"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-iu-" + Guid.NewGuid().ToString("N"));
        try
        {
            var created = (await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath))).Instance;

            var updated = await diff.UpdateInstanceAsync(bk, ik,
                new UpdateInstanceRequest("Renamed", basePath, CredentialProfile: "corp", Enabled: false, created.Version));

            Assert.Equal("Renamed", updated.Name);
            Assert.Equal("corp", updated.CredentialProfile);
            Assert.False(updated.Enabled);
            Assert.Equal(created.Version + 1, updated.Version);
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InstanceStats_ReflectScopedChecks()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "B"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-st-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));
            await diff.CreateCheckAsync(new CreateCheckRequest
            {
                Key = "chk_" + Guid.NewGuid().ToString("N")[..8],
                Type = CheckType.Readiness,
                ScopeKind = CheckScopeKind.Instance,
                BranchKey = bk,
                InstanceKey = ik,
                IntervalSeconds = 60,
            });

            var stats = await diff.GetInstanceStatsAsync(bk, ik);
            Assert.True(stats.Checks.Active >= 1);
            Assert.True(stats.Automations.Active >= 1);
            Assert.Equal(0, stats.Jobs.Running); // no job triggered for this instance
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Trigger_AutoDefault_Run_Idempotency_SoftDelete()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "Branch"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-trg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instance = (await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath))).Instance;
            await File.WriteAllTextAsync(Path.Combine(basePath, "old", "doc.pdf"), "%PDF-1.4 stub");
            await File.WriteAllTextAsync(Path.Combine(basePath, "new", "doc.pdf"), "%PDF-1.4 stub");

            // A default trigger was auto-created over the instance.
            var def = Assert.Single(await diff.ListInstanceTriggersAsync(instance.Id), t => t.IsDefault);
            Assert.Equal(TriggerActionType.RunComparison, def.ActionType);

            // Run → creates + enqueues a batch job and returns its id immediately (no waiting).
            var run = await diff.RunTriggerAsync(def.Id);
            Assert.True(run.Success);
            Assert.NotNull(run.BatchJobId);
            Assert.Equal("queued", run.Status);

            // Idempotency-Key dedupe: the same key returns the original batch job.
            var k1 = await diff.RunTriggerAsync(def.Id, idempotencyKey: "k1");
            var k1again = await diff.RunTriggerAsync(def.Id, idempotencyKey: "k1");
            Assert.Equal(k1.BatchJobId, k1again.BatchJobId);

            Assert.True((await diff.ListTriggerRunsAsync(def.Id)).Count >= 1);
            Assert.NotEmpty(await diff.ListTriggerJobsAsync(def.Id));

            // Soft delete: marked deleted, history preserved, no longer runnable.
            await diff.DeleteTriggerAsync(def.Id);
            Assert.True((await diff.GetTriggerAsync(def.Id))!.IsDeleted);
            Assert.True((await diff.ListTriggerRunsAsync(def.Id)).Count >= 1);
            var blocked = await diff.RunTriggerAsync(def.Id);
            Assert.False(blocked.Success);
            Assert.Equal("TRIGGER_DELETED", blocked.ErrorCode);
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Trigger_Crud_RoundTrip_ViaApi()
    {
        var diff = NewClient();
        var (bk, ik) = FreshKeys();
        await diff.CreateBranchAsync(new(bk, "B"));
        string basePath = Path.Combine(Path.GetTempPath(), "diffpdf-tc-" + Guid.NewGuid().ToString("N"));
        try
        {
            await diff.CreateInstanceAsync(bk, new(ik, "Inst", basePath));

            var created = await diff.CreateTriggerAsync(new CreateTriggerRequest { BranchKey = bk, InstanceKey = ik, Name = "Custom" });
            Assert.False(created.IsDefault);
            Assert.Equal("Custom", created.Name);

            var updated = await diff.UpdateTriggerAsync(created.Id, new UpdateTriggerRequest { Name = "Renamed", Enabled = false });
            Assert.Equal("Renamed", updated.Name);
            Assert.False(updated.Enabled);

            await diff.DeleteTriggerAsync(created.Id);
            Assert.True((await diff.GetTriggerAsync(created.Id))!.IsDeleted);
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch (IOException) { }
        }
    }
}
