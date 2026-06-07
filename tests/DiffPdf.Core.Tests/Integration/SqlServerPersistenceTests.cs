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
}
