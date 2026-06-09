using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Orchestrates "Vytisknout a porovnat": print a fresh sample batch on the Tisk SOA, find the previous
/// comparable batch, fetch both into the instance's <c>old/</c>/<c>new/</c> folders, then hand off to the
/// normal comparison pipeline via <see cref="IBatchLauncher"/> (which sees the now-populated folders).
/// <para>
/// <b>At-most-once printing.</b> Printing has real side effects, so this handler <b>never rethrows</b>:
/// every failure is recorded on the operation and the message is acknowledged, so Wolverine does not
/// redeliver (a redelivery would re-print). The only unguarded window is a process crash mid-handler —
/// the same at-most-once risk the legacy HromadneVzory flow carries. Once the comparison job is launched
/// it is fully durable as usual. (Follow-up: split print/fetch into two messages so fetch/launch can retry.)
/// </para>
/// </summary>
public sealed class PrintAndCompareBatchHandler
{
    public static async Task Handle(
        PrintAndCompareBatch command,
        IBranchStore branches,
        IInstanceStore instances,
        INetworkShareResolver shareResolver,
        IPrintSourceResolver printSources,
        IPrintBatchService printService,
        IPrintOperationStore operations,
        IBatchLauncher launcher,
        ILogger<PrintAndCompareBatchHandler> logger,
        CancellationToken ct)
    {
        var scope = $"{command.BranchKey}/{command.InstanceKey}";
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["PrintOperationId"] = command.OperationId,
            ["Scope"] = scope,
        });

        try
        {
            // 1) Resolve scope → instance → SOA connection → the old/new folders the comparison reads.
            var branch = await branches.GetByKeyAsync(command.BranchKey, ct);
            if (branch is null || !branch.Enabled)
            {
                operations.SetPhase(command.OperationId, PrintPhase.Failed, error: $"Branch '{command.BranchKey}' not found or disabled.");
                return;
            }

            var instance = await instances.GetByKeyAsync(branch.Id, command.InstanceKey, ct);
            if (instance is null || !instance.Enabled)
            {
                operations.SetPhase(command.OperationId, PrintPhase.Failed, error: $"Instance '{scope}' not found or disabled.");
                return;
            }

            PrintSoaConnection conn;
            try
            {
                conn = printSources.Resolve(command.BranchKey, command.InstanceKey);
            }
            catch (InvalidOperationException ex)
            {
                operations.SetPhase(command.OperationId, PrintPhase.Failed, error: ex.Message);
                return;
            }

            ResolvedFolder baseResolved;
            try
            {
                baseResolved = shareResolver.Resolve(instance.BasePath, inlineCredentials: null, credentialProfile: instance.CredentialProfile);
            }
            catch (NetworkConfigurationException ex)
            {
                operations.SetPhase(command.OperationId, PrintPhase.Failed, error: $"Base path unreachable: {ex.Message}");
                return;
            }

            var oldFolder = InstanceFolders.Old(baseResolved.Path);
            var newFolder = InstanceFolders.New(baseResolved.Path);

            // 2) Print a fresh sample batch (AT MOST ONCE — see class remarks).
            operations.SetPhase(command.OperationId, PrintPhase.Printing);
            var print = await printService.PrintSampleAsync(conn, ct);
            logger.LogInformation("Printed sample batch {Batch} for {Scope}. {Validation}", print.BatchNumber, scope, print.ValidationSummary);

            // 3) Find the previous comparable batch (fail clearly when there is none).
            operations.SetPhase(command.OperationId, PrintPhase.FindingPrevious, newBatch: print.BatchNumber, validationSummary: print.ValidationSummary);
            var previous = await printService.FindPreviousBatchAsync(conn, print.BatchNumber, ct);
            if (string.IsNullOrEmpty(previous))
            {
                operations.SetPhase(command.OperationId, PrintPhase.NoPreviousBatch,
                    error: $"Žádná předchozí dávka k porovnání s dávkou '{print.BatchNumber}' nebyla nalezena.");
                logger.LogWarning("No previous batch to compare with {Batch} for {Scope}.", print.BatchNumber, scope);
                return;
            }

            // 4) Fetch both batches into old/new — clear each folder first (like TransferFilesToCompare).
            operations.SetPhase(command.OperationId, PrintPhase.Fetching, previousBatch: previous);
            ResetFolder(oldFolder);
            ResetFolder(newFolder);
            await printService.FetchBatchToFolderAsync(conn, previous, oldFolder, ct);
            await printService.FetchBatchToFolderAsync(conn, print.BatchNumber, newFolder, ct);

            // 5) Launch a normal comparison; its readiness gate now sees the populated old/new folders.
            operations.SetPhase(command.OperationId, PrintPhase.Launching);
            var spec = LaunchSpec.Default with { Source = JobSource.RestApi };
            var result = await launcher.LaunchAsync(command.BranchKey, command.InstanceKey, spec, ct: ct);
            if (!result.Launched)
            {
                operations.SetPhase(command.OperationId, PrintPhase.Failed, error: result.Detail ?? $"Spuštění porovnání selhalo ({result.Outcome}).");
                logger.LogWarning("Print-and-compare for {Scope} fetched batches but launch returned {Outcome}: {Detail}", scope, result.Outcome, result.Detail);
                return;
            }

            operations.SetPhase(command.OperationId, PrintPhase.Completed, jobId: result.JobId);
            logger.LogInformation("Print-and-compare launched job {JobId} for {Scope} (new={New}, previous={Previous}).",
                result.JobId, scope, print.BatchNumber, previous);
        }
        catch (Exception ex)
        {
            // Never rethrow: acknowledging the message prevents a redelivery that would re-print.
            operations.SetPhase(command.OperationId, PrintPhase.Failed, error: ex.Message);
            logger.LogError(ex, "Print-and-compare for {Scope} failed.", scope);
        }
    }

    // Delete + recreate so a stale prior batch never bleeds into the new comparison.
    private static void ResetFolder(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
    }
}
