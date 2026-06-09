using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Printing;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Orchestration of "Vytisknout a porovnat" via a fake <see cref="IPrintBatchService"/> (the real D3Soft
/// proxies can't be unit-tested). Asserts the print → find-previous → fetch(old)+fetch(new) → launch order,
/// the no-previous-batch failure path, and that validations are surfaced on the operation.
/// </summary>
public class PrintAndCompareBatchHandlerTests
{
    private static string NewTempBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "diffpdf-print-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<(InMemoryBranchStore Branches, InMemoryInstanceStore Instances, string BasePath)> SeedScopeAsync(string branchKey, string instanceKey)
    {
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var branch = await branches.CreateAsync(branchKey, branchKey);
        var basePath = NewTempBase();
        await instances.CreateAsync(branch.Id, instanceKey, instanceKey, basePath, credentialProfile: null);
        return (branches, instances, basePath);
    }

    [Fact]
    public async Task Happy_path_prints_finds_previous_fetches_both_then_launches()
    {
        var (branches, instances, basePath) = await SeedScopeAsync("Alfa", "Lama");
        var print = new RecordingPrintBatchService(previousBatch: "B-PREV", newBatch: "B-NEW");
        var launcher = new RecordingBatchLauncher(jobId: Guid.NewGuid());
        var ops = new InMemoryPrintOperationStore();
        var opId = Guid.NewGuid();
        ops.Start(opId, "Alfa", "Lama");

        await PrintAndCompareBatchHandler.Handle(
            new PrintAndCompareBatch(opId, "Alfa", "Lama"),
            branches, instances, new FakeShareResolver(basePath),
            new FakePrintSourceResolver("Alfa", "Lama"), print, ops, launcher,
            NullLogger<PrintAndCompareBatchHandler>.Instance, CancellationToken.None);

        // Order: print → find-previous → fetch(prev→old) → fetch(new→new) → launch.
        Assert.Equal(
            new[] { "print:B-NEW", "find-previous:B-NEW", $"fetch:B-PREV->{InstanceFolders.Old(basePath)}", $"fetch:B-NEW->{InstanceFolders.New(basePath)}" },
            print.Calls);
        Assert.Equal(("Alfa", "Lama"), (launcher.BranchKey, launcher.InstanceKey));

        var status = ops.Get(opId)!;
        Assert.Equal(PrintPhase.Completed, status.Phase);
        Assert.Equal(launcher.JobId, status.JobId);
        Assert.Equal("B-NEW", status.NewBatch);
        Assert.Equal("B-PREV", status.PreviousBatch);
    }

    [Fact]
    public async Task No_previous_batch_fails_cleanly_without_fetching_or_launching()
    {
        var (branches, instances, basePath) = await SeedScopeAsync("Alfa", "Lama");
        var print = new RecordingPrintBatchService(previousBatch: null, newBatch: "B-NEW"); // nothing to compare against
        var launcher = new RecordingBatchLauncher(jobId: Guid.NewGuid());
        var ops = new InMemoryPrintOperationStore();
        var opId = Guid.NewGuid();
        ops.Start(opId, "Alfa", "Lama");

        await PrintAndCompareBatchHandler.Handle(
            new PrintAndCompareBatch(opId, "Alfa", "Lama"),
            branches, instances, new FakeShareResolver(basePath),
            new FakePrintSourceResolver("Alfa", "Lama"), print, ops, launcher,
            NullLogger<PrintAndCompareBatchHandler>.Instance, CancellationToken.None);

        Assert.Equal(new[] { "print:B-NEW", "find-previous:B-NEW" }, print.Calls); // printed + searched, but no fetch
        Assert.False(launcher.WasCalled);
        Assert.Equal(PrintPhase.NoPreviousBatch, ops.Get(opId)!.Phase);
    }

    [Fact]
    public async Task Print_validation_errors_are_surfaced_on_the_operation()
    {
        var (branches, instances, basePath) = await SeedScopeAsync("Alfa", "Lama");
        var print = new RecordingPrintBatchService(previousBatch: "B-PREV", newBatch: "B-NEW")
        {
            BlankPage = new PrintValidation(true, "Validace na prázdné stránky proběhla NEÚSPĚŠNĚ.", ["doc1.pdf"]),
        };
        var launcher = new RecordingBatchLauncher(jobId: Guid.NewGuid());
        var ops = new InMemoryPrintOperationStore();
        var opId = Guid.NewGuid();
        ops.Start(opId, "Alfa", "Lama");

        await PrintAndCompareBatchHandler.Handle(
            new PrintAndCompareBatch(opId, "Alfa", "Lama"),
            branches, instances, new FakeShareResolver(basePath),
            new FakePrintSourceResolver("Alfa", "Lama"), print, ops, launcher,
            NullLogger<PrintAndCompareBatchHandler>.Instance, CancellationToken.None);

        var status = ops.Get(opId)!;
        Assert.Equal(PrintPhase.Completed, status.Phase); // validation is informational, comparison still runs
        Assert.NotNull(status.ValidationSummary);
        Assert.Contains("NEÚSPĚŠNĚ", status.ValidationSummary);
    }

    [Fact]
    public async Task Unconfigured_or_missing_scope_records_failure_and_does_not_launch()
    {
        var (branches, instances, basePath) = await SeedScopeAsync("Alfa", "Lama");
        var print = new RecordingPrintBatchService(previousBatch: "B-PREV", newBatch: "B-NEW");
        var launcher = new RecordingBatchLauncher(jobId: Guid.NewGuid());
        var ops = new InMemoryPrintOperationStore();
        var opId = Guid.NewGuid();
        ops.Start(opId, "Alfa", "Ghost");

        await PrintAndCompareBatchHandler.Handle(
            new PrintAndCompareBatch(opId, "Alfa", "Ghost"), // instance doesn't exist
            branches, instances, new FakeShareResolver(basePath),
            new FakePrintSourceResolver("Alfa", "Ghost"), print, ops, launcher,
            NullLogger<PrintAndCompareBatchHandler>.Instance, CancellationToken.None);

        Assert.Empty(print.Calls);          // never printed
        Assert.False(launcher.WasCalled);
        Assert.Equal(PrintPhase.Failed, ops.Get(opId)!.Phase);
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class RecordingPrintBatchService(string? previousBatch, string newBatch) : IPrintBatchService
    {
        public List<string> Calls { get; } = [];
        public PrintValidation BlankPage { get; set; } = new(false, "Blank OK.", []);
        public PrintValidation TextPattern { get; set; } = new(false, "Text OK.", []);

        public Task<PrintSampleResult> PrintSampleAsync(PrintSoaConnection conn, CancellationToken ct = default)
        {
            Calls.Add($"print:{newBatch}");
            return Task.FromResult(new PrintSampleResult(newBatch, BlankPage, TextPattern));
        }

        public Task<string?> FindPreviousBatchAsync(PrintSoaConnection conn, string currentBatch, CancellationToken ct = default)
        {
            Calls.Add($"find-previous:{currentBatch}");
            return Task.FromResult(previousBatch);
        }

        public Task FetchBatchToFolderAsync(PrintSoaConnection conn, string batchNumber, string destinationFolder, CancellationToken ct = default)
        {
            Calls.Add($"fetch:{batchNumber}->{destinationFolder}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBatchLauncher(Guid jobId) : IBatchLauncher
    {
        public Guid JobId { get; } = jobId;
        public bool WasCalled { get; private set; }
        public string? BranchKey { get; private set; }
        public string? InstanceKey { get; private set; }

        public Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, bool enqueueOnly = false, CancellationToken ct = default)
        {
            WasCalled = true;
            BranchKey = branchKey;
            InstanceKey = instanceKey;
            return Task.FromResult(new LaunchResult(LaunchOutcome.Launched, JobId));
        }
    }

    private sealed class FakeShareResolver(string path) : INetworkShareResolver
    {
        public ResolvedFolder Resolve(string folder, NetworkCredentials? inlineCredentials = null, string? credentialProfile = null) =>
            new(path, null);
        public IReadOnlyList<ShareInfo> ListShares() => [];
        public IReadOnlyList<string> ListCredentialProfiles() => [];
    }

    private sealed class FakePrintSourceResolver(string branchKey, string instanceKey) : IPrintSourceResolver
    {
        public bool IsConfigured(string b, string i) => b == branchKey && i == instanceKey;
        public IReadOnlyCollection<string> ConfiguredScopes => [TiskOptions.ScopeKey(branchKey, instanceKey)];
        public PrintSoaConnection Resolve(string b, string i) => new("https://soa", "client", "user", "pwd");
    }
}
