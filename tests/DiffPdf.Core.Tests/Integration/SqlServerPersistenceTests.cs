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
}
