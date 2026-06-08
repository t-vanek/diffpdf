using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>Runs a single control check: executes it, records the run, updates the check, and notifies.</summary>
public interface IControlCheckRunner
{
    /// <summary>Executes <paramref name="check"/> now (used by both the background runner and the run-now endpoint).</summary>
    Task<ControlCheckRun> RunAsync(ControlCheck check, CancellationToken ct = default);
}
