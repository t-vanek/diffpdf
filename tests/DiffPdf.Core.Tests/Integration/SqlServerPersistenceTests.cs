using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Automation;
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
    public async Task Branch_Instance_Schedule_Watch_RoundTrip()
    {
        await using var ctx = db.NewContext();
        var branches = new SqlServerBranchStore(ctx, _mapper);
        var instances = new SqlServerInstanceStore(ctx, _mapper);
        var schedules = new SqlServerScheduleStore(ctx, _mapper);
        var watches = new SqlServerWatchStore(ctx, _mapper);

        string bKey = "b_" + Guid.NewGuid().ToString("N")[..8];
        string basePath = $@"C:\root\{bKey}\inst1";
        var branch = await branches.CreateAsync(bKey, bKey);
        var instance = await instances.CreateAsync(branch.Id, "inst1", "Instance One", basePath, null);

        var fetchedBranch = await branches.GetByKeyAsync(bKey);
        Assert.Equal(branch.Id, fetchedBranch!.Id);

        var fetchedInstance = await instances.GetByKeyAsync(branch.Id, "inst1");
        Assert.Equal(basePath, fetchedInstance!.BasePath);

        await schedules.CreateAsync(new ComparisonSchedule
        {
            Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id,
            BranchKey = branch.Key, InstanceKey = instance.Key,
            Key = "nightly", Name = "Nightly", Cron = "0 2 * * *", Enabled = false,
        });
        var fetchedSchedule = await schedules.GetByKeyAsync(instance.Id, "nightly");
        Assert.False(fetchedSchedule!.Enabled);
        Assert.Equal("0 2 * * *", fetchedSchedule.Cron);

        await watches.UpsertAsync(new FolderWatch
        {
            Id = Guid.NewGuid(), BranchId = branch.Id, InstanceId = instance.Id,
            BranchKey = branch.Key, InstanceKey = instance.Key, StabilitySeconds = 45, Enabled = false,
        });
        var fetchedWatch = await watches.GetByInstanceAsync(instance.Id);
        Assert.Equal(45, fetchedWatch!.StabilitySeconds);
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
    public async Task ScopeSync_Apply_RegistersAndProvisionsDefaultsInRealDb()
    {
        string root = Path.Combine(Path.GetTempPath(), "diffpdf-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "alfa", "inst1", "old"));
        Directory.CreateDirectory(Path.Combine(root, "alfa", "inst1", "new"));
        try
        {
            await using var ctx = db.NewContext();
            var branches = new SqlServerBranchStore(ctx, _mapper);
            var instances = new SqlServerInstanceStore(ctx, _mapper);
            var schedules = new SqlServerScheduleStore(ctx, _mapper);
            var watches = new SqlServerWatchStore(ctx, _mapper);

            var net = Options.Create(new NetworkOptions());
            var resolver = new NetworkShareResolver(net);
            var connector = new PlatformShareConnector(net, NullLogger<PlatformShareConnector>.Instance);
            var structure = new InstanceStructureService(resolver, connector, NullLogger<InstanceStructureService>.Instance);

            // Default automation enabled: a disabled schedule + watch should be provisioned (no initial trigger here).
            var provisioner = new DefaultAutomationProvisioner(schedules, watches, new StubBatchLauncher(),
                Options.Create(new DefaultAutomationOptions { Enabled = true, FireInitialTrigger = false }),
                NullLogger<DefaultAutomationProvisioner>.Instance);

            var sync = new ScopeSyncService(resolver, connector, branches, instances, structure, provisioner,
                Options.Create(new ScopeSyncOptions { RootPath = root }), NullLogger<ScopeSyncService>.Instance);

            var report = await sync.SynchronizeAsync(apply: true);

            Assert.True(report.Ok);
            var branch = await branches.GetByKeyAsync("alfa");
            Assert.NotNull(branch);
            var instance = await instances.GetByKeyAsync(branch!.Id, "inst1");
            Assert.NotNull(instance);
            // Default automation landed the disabled schedule + watch in the real database.
            Assert.NotNull(await schedules.GetByKeyAsync(instance!.Id, "default"));
            Assert.NotNull(await watches.GetByInstanceAsync(instance.Id));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
