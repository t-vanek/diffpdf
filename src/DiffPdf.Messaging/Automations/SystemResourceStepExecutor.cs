using System.Diagnostics;
using System.Globalization;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Monitors a single system resource against thresholds and notifies on pressure — the resource is chosen
/// by the <c>resource</c> parameter:
/// <list type="bullet">
///   <item><c>disk</c> — free space on the storage-root volume; <c>warnPercent</c>/<c>failPercent</c> are
///   the <b>minimum</b> free percentages (Warning/Failed when free space drops below them).</item>
///   <item><c>cpu</c> — this process's CPU usage, sampled over a short window; thresholds are the
///   <b>maximum</b> load percentages.</item>
///   <item><c>ram</c> — machine memory load (GC view of committed vs. available); thresholds are the
///   <b>maximum</b> load percentages.</item>
/// </list>
/// Read-only: it never changes anything, only reports. Defaults: <c>warnPercent</c> 85, <c>failPercent</c> 95.
/// </summary>
public sealed class SystemResourceStepExecutor(IOptions<StorageOptions> storage) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.SystemResource;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        string resource = StepParameters.GetString(step, "resource", "disk").ToLowerInvariant();
        int warnPercent = StepParameters.GetInt(step, "warnPercent", 85);
        int failPercent = StepParameters.GetInt(step, "failPercent", 95);

        return resource switch
        {
            "disk" => CheckDisk(warnPercent, failPercent),
            "cpu" => await CheckCpuAsync(warnPercent, failPercent, ct),
            "ram" => CheckMemory(warnPercent, failPercent),
            _ => StepResult.Failed($"unknown resource '{resource}' (expected disk, cpu or ram)."),
        };
    }

    // Disk: thresholds are MINIMUM free %. Below failPercent -> Failed, below warnPercent -> Warning.
    private StepResult CheckDisk(int warnPercent, int failPercent)
    {
        string root = storage.Value.RootPath;
        try
        {
            string driveRoot = Path.GetPathRoot(Path.GetFullPath(root)) ?? root;
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return StepResult.Warning($"disk: drive '{drive.Name}' not ready.");

            double freePercent = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
            string detail = $"disk '{drive.Name}': {Pct(freePercent)} free ({Gb(drive.AvailableFreeSpace)} of {Gb(drive.TotalSize)}).";

            if (freePercent < failPercent)
                return StepResult.Failed($"low disk space — {detail} below {failPercent}%.");
            if (freePercent < warnPercent)
                return StepResult.Warning($"disk space getting low — {detail} below {warnPercent}%.");
            return StepResult.Ok(detail);
        }
        catch (Exception ex)
        {
            return StepResult.Failed($"disk ({root}): {ex.Message}");
        }
    }

    // CPU: thresholds are MAXIMUM load %. Above failPercent -> Failed, above warnPercent -> Warning.
    private static async Task<StepResult> CheckCpuAsync(int warnPercent, int failPercent, CancellationToken ct)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            var startCpu = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            await Task.Delay(500, ct);
            sw.Stop();
            proc.Refresh();
            double usedMs = (proc.TotalProcessorTime - startCpu).TotalMilliseconds;
            double availableMs = sw.Elapsed.TotalMilliseconds * Math.Max(1, Environment.ProcessorCount);
            double loadPercent = availableMs > 0 ? Math.Clamp(100.0 * usedMs / availableMs, 0, 100) : 0;
            string detail = $"process CPU: {Pct(loadPercent)} over {Environment.ProcessorCount} core(s).";

            if (loadPercent > failPercent)
                return StepResult.Failed($"high CPU — {detail} above {failPercent}%.");
            if (loadPercent > warnPercent)
                return StepResult.Warning($"elevated CPU — {detail} above {warnPercent}%.");
            return StepResult.Ok(detail);
        }
        catch (Exception ex)
        {
            return StepResult.Failed($"cpu: {ex.Message}");
        }
    }

    // RAM: thresholds are MAXIMUM load %. Above failPercent -> Failed, above warnPercent -> Warning.
    private static StepResult CheckMemory(int warnPercent, int failPercent)
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes <= 0)
                return StepResult.Warning("ram: memory info unavailable.");

            double loadPercent = Math.Clamp(100.0 * info.MemoryLoadBytes / info.TotalAvailableMemoryBytes, 0, 100);
            string detail = $"machine memory: {Pct(loadPercent)} used ({Gb(info.MemoryLoadBytes)} of {Gb(info.TotalAvailableMemoryBytes)}).";

            if (loadPercent > failPercent)
                return StepResult.Failed($"high memory — {detail} above {failPercent}%.");
            if (loadPercent > warnPercent)
                return StepResult.Warning($"elevated memory — {detail} above {warnPercent}%.");
            return StepResult.Ok(detail);
        }
        catch (Exception ex)
        {
            return StepResult.Failed($"ram: {ex.Message}");
        }
    }

    private static string Pct(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Gb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
}
