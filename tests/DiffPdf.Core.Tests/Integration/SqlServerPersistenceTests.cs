using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Persistence.SqlServer;
using DiffPdf.Persistence.SqlServer.Mapping;
using DiffPdf.Pdf.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests.Integration;

/// <summary>
/// Real-database integration tests against SQL Server LocalDB: the EF Core migration baseline applies to a
/// fresh database, the SQL Server stores round-trip, unique constraints surface as <see cref="DuplicateKeyException"/>,
/// and scope sync + default-automation provisioning land rows in the real database. Skipped when LocalDB is absent.
/// </summary>
[Collection(LocalDbCollection.Name)]
public class SqlServerPersistenceTests(LocalDbFixture db)
{
    private readonly EntityMapper _mapper = new();

    [LocalDbFact]
    public async Task Migrations_Apply_AndLeaveNoPendingMigrations()
    {
        await using var ctx = db.NewContext();
        Assert.True(await ctx.Database.CanConnectAsync());
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); // InitialCreate applied by the fixture
    }

    [LocalDbFact]
    public async Task Branch_Instance_Check_RoundTrip()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        var instances = new SqlServerInstanceStore(ctx, _mapper);
        var checks = new SqlServerControlCheckStore(ctx, _mapper);

        string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
        string basePath = $@"C:\root\{bKey}\inst1";
        var branch = await branches.CreateAsync(bKey, bKey);
        var instance = await instances.CreateAsync(branch.Id, "inst1", "Instance One", basePath, null);

        var fetchedBranch = await branches.GetByKeyAsync(bKey);
        Assert.Equal(branch.Id, fetchedBranch!.Id);

        var fetchedInstance = await instances.GetByKeyAsync(branch.Id, "inst1");
        Assert.Equal(basePath, fetchedInstance!.BasePath);

        string checkKey = "rd_" + Guid.NewGuid().ToString("N")[..8];
        await checks.CreateAsync(new ControlCheck
        {
            Id = Guid.NewGuid(), Key = checkKey, Name = "Readiness", Type = CheckType.Readiness,
            ScopeKind = CheckScopeKind.Instance, BranchKey = branch.Key, InstanceKey = instance.Key,
            IntervalSeconds = 300, Events = [NotificationEvent.ReadinessFailed],
            Parameters = new Dictionary<string, string> { ["k"] = "v" }, Enabled = false,
        });

        var fetched = await checks.GetByKeyAsync(checkKey);
        Assert.False(fetched!.Enabled);
        Assert.Equal(CheckType.Readiness, fetched.Type);
        Assert.Equal("v", fetched.Parameters["k"]);
        Assert.Contains(NotificationEvent.ReadinessFailed, fetched.Events);
    }

    [LocalDbFact]
    public async Task DuplicateBranchKey_ThrowsDuplicateKey()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        string bKey = "dup_" + Guid.NewGuid().ToString("N")[..8];
        await branches.CreateAsync(bKey, bKey);
        await Assert.ThrowsAsync<DuplicateKeyException>(() => branches.CreateAsync(bKey, bKey));
    }

    [LocalDbFact]
    public async Task DuplicateBranchName_ThrowsDuplicateKey()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        string name = "DupName_" + Guid.NewGuid().ToString("N")[..8];
        await branches.CreateAsync("k1_" + Guid.NewGuid().ToString("N")[..8], name);
        // Different key, same name → the unique name index rejects it (surfaced as DuplicateKeyException).
        await Assert.ThrowsAsync<DuplicateKeyException>(
            () => branches.CreateAsync("k2_" + Guid.NewGuid().ToString("N")[..8], name));
    }

    [LocalDbFact]
    public async Task DuplicateInstanceName_InSameBranch_ThrowsDuplicateKey()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        var instances = new SqlServerInstanceStore(ctx, _mapper);
        var branch = await branches.CreateAsync("ib_" + Guid.NewGuid().ToString("N")[..8], "ib_" + Guid.NewGuid().ToString("N")[..8]);
        const string name = "DupInst";
        await instances.CreateAsync(branch.Id, "i1", name, @"C:\x\1", null);
        await Assert.ThrowsAsync<DuplicateKeyException>(
            () => instances.CreateAsync(branch.Id, "i2", name, @"C:\x\2", null));
    }

    [LocalDbFact]
    public async Task InstanceCountByBranch_Translates_OnRealDb()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        var instances = new SqlServerInstanceStore(ctx, _mapper);
        var branch = await branches.CreateAsync("cb_" + Guid.NewGuid().ToString("N")[..8], "cb_" + Guid.NewGuid().ToString("N")[..8]);
        await instances.CreateAsync(branch.Id, "i1", "i1", @"C:\x\1", null);
        await instances.CreateAsync(branch.Id, "i2", "i2", @"C:\x\2", null);

        // Exercises the GroupBy translation used by GET /branches/summary's batch path.
        var counts = await instances.CountByBranchAsync();
        Assert.Equal(2, counts[branch.Id]);
    }

    [LocalDbFact]
    public async Task ScopeSync_Apply_RegistersInRealDb()
    {
        string root = Path.Combine(Path.GetTempPath(), "diffpdf-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "alfa", "inst1", "old"));
        Directory.CreateDirectory(Path.Combine(root, "alfa", "inst1", "new"));
        try
        {
            await using var ctx = db.NewContext();
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);

            var net = Options.Create(new NetworkOptions());
            var resolver = new NetworkShareResolver(net);
            var connector = new PlatformShareConnector(net, NullLogger<PlatformShareConnector>.Instance);
            var structure = new InstanceStructureService(resolver, connector, NullLogger<InstanceStructureService>.Instance);

            var sync = new ScopeSyncService(resolver, connector, branches, instances, structure,
                new NoopControlCheckProvisioner(), Options.Create(new ScopeSyncOptions { RootPath = root }), NullLogger<ScopeSyncService>.Instance);

            var report = await sync.SynchronizeAsync(apply: true);

            Assert.True(report.Ok);
            var branch = await branches.GetByKeyAsync("alfa");
            Assert.NotNull(branch);
            var instance = await instances.GetByKeyAsync(branch!.Id, "inst1");
            Assert.NotNull(instance);
            // The reports/ skeleton was created on disk for the newly registered instance.
            Assert.True(Directory.Exists(Path.Combine(root, "alfa", "inst1", "reports")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [LocalDbFact]
    public async Task JobVerdict_IsDenormalized_InListProjection_AndBackfillRestoresIt()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        var instances = new SqlServerInstanceStore(ctx, _mapper);
        var jobs = new SqlServerJobStore(ctx, _mapper);

        string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
        var branch = await branches.CreateAsync(bKey, bKey);
        var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);

        var job = await jobs.CreateAsync(new ComparisonJob
        {
            Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
            Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
        });
        var started = await jobs.TryStartAsync(job.Id, "w1", TimeSpan.FromMinutes(5));
        var report = new BatchComparisonReport
        {
            OldFolder = @"C:\old", NewFolder = @"C:\new",
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Files =
            [
                new FilePairResult { RelativePath = "a.pdf", Status = FilePairStatus.Differs },
                new FilePairResult { RelativePath = "b.pdf", Status = FilePairStatus.Identical },
                new FilePairResult { RelativePath = "c.pdf", Status = FilePairStatus.Error },
            ],
        };
        await jobs.CompleteAsync(job.Id, report, started!.Version);

        // The list projection carries the verdict + joined keys — no request/report JSON deserialized.
        var row = (await jobs.ListSummariesAsync(new JobListQuery { BranchKey = branch.Key })).Single(j => j.Id == job.Id);
        Assert.Equal(branch.Key, row.BranchKey);
        Assert.Equal(instance.Key, row.InstanceKey);
        Assert.Equal(JobStatus.Completed, row.Status);
        Assert.Equal(report.Differing, row.Differing);
        Assert.Equal(report.Errors, row.Errors);
        Assert.Equal(report.Passed, row.GatePassed);

        // Simulate a pre-migration row (verdict columns null but report present), then backfill — on fresh
        // contexts so EF's change tracker doesn't hide the direct SQL update (mirrors the startup runner).
        await using (var raw = db.NewContext())
            await raw.Database.ExecuteSqlRawAsync(
                "UPDATE jobs SET DifferingCount = NULL, ErrorCount = NULL, GatePassed = NULL WHERE Id = {0}", job.Id);

        await using (var fresh = db.NewContext())
            Assert.True(await new SqlServerJobStore(fresh, _mapper).BackfillVerdictsAsync(100) >= 1);

        await using var verify = db.NewContext();
        var after = (await new SqlServerJobStore(verify, _mapper)
            .ListSummariesAsync(new JobListQuery { BranchKey = branch.Key })).Single(j => j.Id == job.Id);
        Assert.Equal(report.Differing, after.Differing);
        Assert.Equal(report.Errors, after.Errors);
    }

    [LocalDbFact]
    public async Task SkipPendingForTerminalJobs_HealsStrandedQueuedPairs_NotRunningOnes()
    {
        Guid cancelledJobId, runningJobId;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);
            var tasks = new SqlServerFilePairTaskStore(ctx, _mapper);

            string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(bKey, bKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);

            ComparisonJob NewJob() => new()
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            };

            var cancelled = await jobs.CreateAsync(NewJob());
            await jobs.TryStartAsync(cancelled.Id, "w1", TimeSpan.FromMinutes(5));
            await jobs.CancelAsync(cancelled.Id);
            cancelledJobId = cancelled.Id;

            var running = await jobs.CreateAsync(NewJob());
            await jobs.TryStartAsync(running.Id, "w2", TimeSpan.FromMinutes(5)); // stays Running
            runningJobId = running.Id;

            await tasks.CreateManyAsync(
            [
                new FilePairTask { Id = Guid.NewGuid(), JobId = cancelledJobId, RelativePath = "a.pdf", Status = FilePairTaskStatus.Queued },
                new FilePairTask { Id = Guid.NewGuid(), JobId = cancelledJobId, RelativePath = "b.pdf", Status = FilePairTaskStatus.Completed },
                new FilePairTask { Id = Guid.NewGuid(), JobId = runningJobId, RelativePath = "c.pdf", Status = FilePairTaskStatus.Queued },
            ]);
        }

        // Fresh context (mirrors the startup runner; avoids EF change-tracker staleness after the set-based update).
        await using (var sweep = db.NewContext())
            Assert.True(await new SqlServerFilePairTaskStore(sweep, _mapper).SkipPendingForTerminalJobsAsync() >= 1);

        await using var verify = db.NewContext();
        var taskStore = new SqlServerFilePairTaskStore(verify, _mapper);
        var cancelledTasks = await taskStore.ListByJobAsync(cancelledJobId);
        Assert.DoesNotContain(cancelledTasks, t => t.Status == FilePairTaskStatus.Queued); // stranded → skipped
        Assert.Contains(cancelledTasks, t => t.Status == FilePairTaskStatus.Skipped);
        Assert.Contains(cancelledTasks, t => t.Status == FilePairTaskStatus.Completed);    // untouched
        // The still-Running job keeps its Queued pair — only terminal jobs are swept.
        Assert.Contains(await taskStore.ListByJobAsync(runningJobId), t => t.Status == FilePairTaskStatus.Queued);
    }

    [LocalDbFact]
    public async Task SkipPendingForTerminalJobs_DoesNotSkip_PausedJobsQueuedPairs()
    {
        Guid pausedJobId;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);
            var tasks = new SqlServerFilePairTaskStore(ctx, _mapper);

            string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(bKey, bKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);

            var paused = await jobs.CreateAsync(new ComparisonJob
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            });
            await jobs.TryStartAsync(paused.Id, "w1", TimeSpan.FromMinutes(5));
            await jobs.PauseAsync(paused.Id);
            pausedJobId = paused.Id;

            await tasks.CreateManyAsync(
            [
                new FilePairTask { Id = Guid.NewGuid(), JobId = pausedJobId, RelativePath = "a.pdf", Status = FilePairTaskStatus.Queued },
            ]);
        }

        await using (var sweep = db.NewContext())
            await new SqlServerFilePairTaskStore(sweep, _mapper).SkipPendingForTerminalJobsAsync();

        // Paused is NOT terminal — resume relies on these pairs staying Queued; the heal must leave them alone.
        await using var verify = db.NewContext();
        Assert.Contains(await new SqlServerFilePairTaskStore(verify, _mapper).ListByJobAsync(pausedJobId),
            t => t.Status == FilePairTaskStatus.Queued);
    }

    [LocalDbFact]
    public async Task Complete_OnAlreadyCompletedTask_ReturnsFalse_AndKeepsFirstResult()
    {
        Guid jobId, taskId;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);
            var tasks = new SqlServerFilePairTaskStore(ctx, _mapper);

            string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(bKey, bKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);
            var job = await jobs.CreateAsync(new ComparisonJob
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            });
            await jobs.TryStartAsync(job.Id, "w1", TimeSpan.FromMinutes(5));
            jobId = job.Id;

            var task = new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "a.pdf", Status = FilePairTaskStatus.Queued };
            await tasks.CreateManyAsync([task]);
            taskId = task.Id;
            await tasks.TryClaimAsync(taskId, "w1", TimeSpan.FromMinutes(5)); // Queued → Running
        }

        await using (var first = db.NewContext())
            Assert.True(await new SqlServerFilePairTaskStore(first, _mapper).CompleteAsync(taskId,
                new FilePairResult { RelativePath = "a.pdf", Status = FilePairStatus.Differs, DifferingPages = 2 },
                FilePairTaskStatus.Completed)); // won the transition

        await using (var second = db.NewContext())
            Assert.False(await new SqlServerFilePairTaskStore(second, _mapper).CompleteAsync(taskId,
                new FilePairResult { RelativePath = "a.pdf", Status = FilePairStatus.Identical, DifferingPages = 99 },
                FilePairTaskStatus.Completed)); // already terminal → no-op

        await using var verify = db.NewContext();
        var stored = Assert.Single(await new SqlServerFilePairTaskStore(verify, _mapper).ListByJobAsync(jobId));
        Assert.Equal(FilePairStatus.Differs, stored.Result!.Status);
        Assert.Equal(2, stored.Result!.DifferingPages);
    }

    [LocalDbFact]
    public async Task ListRunningFullyProcessed_SurfacesDoneButUnfinalizedJobs()
    {
        Guid doneJobId, processingJobId;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);

            string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(bKey, bKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);
            ComparisonJob NewJob() => new()
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            };

            var done = await jobs.CreateAsync(NewJob());
            await jobs.TryStartAsync(done.Id, "w1", TimeSpan.FromMinutes(5));
            await jobs.SetTotalAsync(done.Id, 2);
            await jobs.IncrementProcessedAsync(done.Id);
            await jobs.IncrementProcessedAsync(done.Id); // 2/2, still Running (finalize lost)
            doneJobId = done.Id;

            var processing = await jobs.CreateAsync(NewJob());
            await jobs.TryStartAsync(processing.Id, "w2", TimeSpan.FromMinutes(5));
            await jobs.SetTotalAsync(processing.Id, 2);
            await jobs.IncrementProcessedAsync(processing.Id); // 1/2 — still processing
            processingJobId = processing.Id;
        }

        await using var verify = db.NewContext();
        var store = new SqlServerJobStore(verify, _mapper);

        var pending = await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(10), 50); // force-stale cutoff
        Assert.Contains(pending, j => j.Id == doneJobId);
        Assert.DoesNotContain(pending, j => j.Id == processingJobId);

        var fresh = await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(-10), 50); // just-updated
        Assert.DoesNotContain(fresh, j => j.Id == doneJobId); // idle-grace not yet elapsed
    }

    [LocalDbFact]
    public async Task ListStaleQueued_FindsQueuedPairsUnderStaleRunningJobs_NotPausedNorFresh()
    {
        Guid runningJobId, pausedJobId, runningTaskId;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);
            var tasks = new SqlServerFilePairTaskStore(ctx, _mapper);

            string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(bKey, bKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);
            ComparisonJob NewJob() => new()
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            };

            var running = await jobs.CreateAsync(NewJob());
            await jobs.TryStartAsync(running.Id, "w1", TimeSpan.FromMinutes(5));
            await jobs.SetTotalAsync(running.Id, 2); // indexed (TotalCount > 0)
            runningJobId = running.Id;

            var paused = await jobs.CreateAsync(NewJob());
            await jobs.TryStartAsync(paused.Id, "w2", TimeSpan.FromMinutes(5));
            await jobs.SetTotalAsync(paused.Id, 2);
            await jobs.PauseAsync(paused.Id); // Paused — its Queued pairs must stay put (resume re-dispatches them)
            pausedJobId = paused.Id;

            var t = new FilePairTask { Id = Guid.NewGuid(), JobId = runningJobId, RelativePath = "a.pdf", Status = FilePairTaskStatus.Queued };
            runningTaskId = t.Id;
            await tasks.CreateManyAsync(
            [
                t,
                new FilePairTask { Id = Guid.NewGuid(), JobId = pausedJobId, RelativePath = "b.pdf", Status = FilePairTaskStatus.Queued },
            ]);
        }

        await using var verify = db.NewContext();
        var store = new SqlServerFilePairTaskStore(verify, _mapper);

        var stranded = await store.ListStaleQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(10), 100); // force-stale cutoff
        Assert.Contains(stranded, x => x.TaskId == runningTaskId);
        Assert.DoesNotContain(stranded, x => x.JobId == pausedJobId); // Paused job excluded

        var fresh = await store.ListStaleQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(-10), 100); // just-updated
        Assert.DoesNotContain(fresh, x => x.TaskId == runningTaskId); // idle-grace not yet elapsed
    }

    [LocalDbFact]
    public async Task RequeueRunningTasks_FiltersByWorker_OrRequeuesAll_OnRealDb()
    {
        Guid jobId, mineId, foreignId;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);
            var tasks = new SqlServerFilePairTaskStore(ctx, _mapper);

            string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(bKey, bKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);
            var job = await jobs.CreateAsync(new ComparisonJob
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Queued,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            });
            await jobs.TryStartAsync(job.Id, "w1", TimeSpan.FromMinutes(5));
            jobId = job.Id;

            var mine = new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "mine.pdf", Status = FilePairTaskStatus.Queued };
            var foreign = new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "foreign.pdf", Status = FilePairTaskStatus.Queued };
            mineId = mine.Id;
            foreignId = foreign.Id;
            await tasks.CreateManyAsync([mine, foreign]);
            await tasks.TryClaimAsync(mineId, "me", TimeSpan.FromMinutes(5));       // Running, LockedBy = me
            await tasks.TryClaimAsync(foreignId, "other", TimeSpan.FromMinutes(5)); // Running, LockedBy = other
        }

        // Worker-scoped (graceful shutdown): releases ONLY this worker's pair; the foreign one stays Running.
        await using (var scoped = db.NewContext())
        {
            var released = await new SqlServerFilePairTaskStore(scoped, _mapper).RequeueRunningTasksAsync("me");
            Assert.Equal((jobId, mineId), Assert.Single(released));
        }
        await using (var verify = db.NewContext())
        {
            var after = await new SqlServerFilePairTaskStore(verify, _mapper).ListByJobAsync(jobId);
            Assert.Equal(FilePairTaskStatus.Queued, after.Single(t => t.Id == mineId).Status);     // released
            Assert.Equal(FilePairTaskStatus.Running, after.Single(t => t.Id == foreignId).Status); // untouched (filtered out)
        }

        // Null-scoped (startup orphan reclaim): re-run "me"'s pair, then release ALL Running regardless of worker.
        await using (var reclaim = db.NewContext())
            await new SqlServerFilePairTaskStore(reclaim, _mapper).TryClaimAsync(mineId, "me", TimeSpan.FromMinutes(5));
        await using (var all = db.NewContext())
        {
            var released = await new SqlServerFilePairTaskStore(all, _mapper).RequeueRunningTasksAsync(null);
            Assert.Equal(2, released.Count);
            Assert.Contains((jobId, mineId), released);
            Assert.Contains((jobId, foreignId), released);
        }
    }

    [LocalDbFact]
    public async Task MarkRecovered_StampsRecoveredAt_WriteOnce_AndProjectsInSummary_OnRealDb()
    {
        Guid jobId; string branchKey;
        await using (var ctx = db.NewContext())
        {
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var jobs = new SqlServerJobStore(ctx, _mapper);

            branchKey = "b_" + Guid.NewGuid().ToString("N")[..8];
            var branch = await branches.CreateAsync(branchKey, branchKey);
            var instance = await instances.CreateAsync(branch.Id, "i1", "I1", @"C:\x\1", null);
            var job = await jobs.CreateAsync(new ComparisonJob
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id, Status = JobStatus.Running,
                Request = new BatchComparisonRequest { Scope = new JobScope(branch.Key, instance.Key) },
            });
            jobId = job.Id;
        }

        DateTimeOffset? firstStamp;
        await using (var mark = db.NewContext())
            await new SqlServerJobStore(mark, _mapper).MarkRecoveredAsync([jobId]);
        await using (var verify = db.NewContext())
        {
            var store = new SqlServerJobStore(verify, _mapper);
            firstStamp = (await store.GetAsync(jobId))!.RecoveredAt;
            Assert.NotNull(firstStamp); // stamped on the job

            // The list projection carries RecoveredAt — drives the client's "Obnoveno" chip without opening the job.
            var summary = (await store.ListSummariesAsync(new JobListQuery { BranchKey = branchKey })).Single(j => j.Id == jobId);
            Assert.Equal(firstStamp, summary.RecoveredAt);
        }

        // Write-once (RecoveredAt IS NULL guard): a second recovery leaves the first stamp untouched.
        await using (var again = db.NewContext())
            await new SqlServerJobStore(again, _mapper).MarkRecoveredAsync([jobId]);
        await using (var verify2 = db.NewContext())
            Assert.Equal(firstStamp, (await new SqlServerJobStore(verify2, _mapper).GetAsync(jobId))!.RecoveredAt);
    }
}
