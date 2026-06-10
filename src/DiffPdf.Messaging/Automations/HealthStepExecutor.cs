using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Verifies the server's critical dependencies are usable: the database (a cheap job-count ping), at
/// least one rendering backend, and a write probe to the storage root. The same checks the readiness
/// probe reports, runnable on a cadence with notification on degradation.
/// </summary>
public sealed class HealthStepExecutor(
    IJobStore jobs,
    IEnumerable<IPdfPageRenderer> renderers,
    IOptions<StorageOptions> storage) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.Health;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        var problems = new List<string>();

        try
        {
            await jobs.CountByStatusAsync(ct);
        }
        catch (Exception ex)
        {
            problems.Add($"database: {ex.Message}");
        }

        try
        {
            var healths = new List<RendererHealth>();
            foreach (var r in renderers)
                healths.Add(await r.CheckAsync(ct));
            if (healths.Count == 0 || !healths.Any(h => h.Available))
                problems.Add("renderer: no available backend");
        }
        catch (Exception ex)
        {
            problems.Add($"renderer: {ex.Message}");
        }

        string root = storage.Value.RootPath;
        try
        {
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, $".diffpdf-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            problems.Add($"storage ({root}): {ex.Message}");
        }

        return problems.Count == 0
            ? StepResult.Ok("database, renderer and storage healthy.")
            : StepResult.Failed($"degraded dependencies:\n  - {string.Join("\n  - ", problems)}");
    }
}
