using DiffPdf.Core.Models;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>The outcome of running one control check, with a human-readable detail.</summary>
public sealed record CheckResult(CheckRunOutcome Outcome, string Detail)
{
    public static CheckResult Ok(string detail) => new(CheckRunOutcome.Ok, detail);
    public static CheckResult Warning(string detail) => new(CheckRunOutcome.Warning, detail);
    public static CheckResult Failed(string detail) => new(CheckRunOutcome.Failed, detail);
}

/// <summary>Executes one <see cref="CheckType"/> of control check. Resolved by the runner via <see cref="Type"/>.</summary>
public interface IControlCheckExecutor
{
    /// <summary>The check type this executor handles.</summary>
    CheckType Type { get; }

    /// <summary>Runs the check and returns its outcome. Must not throw for expected failures — return a Failed result.</summary>
    Task<CheckResult> ExecuteAsync(ControlCheck check, CancellationToken ct);
}
