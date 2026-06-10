using System.Diagnostics;
using System.Diagnostics.Metrics;
using DiffPdf.Core.Models;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Metrics for the automation engine, published through the <c>DiffPdf.Automation</c> meter (scraped at
/// <c>/metrics</c> and/or pushed over OTLP). Registered as a singleton. The failing-automations gauge is
/// backed by an in-memory snapshot the engine refreshes every leading tick, so a scrape never blocks on
/// the database.
/// </summary>
public sealed class AutomationMetrics : IDisposable
{
    public const string MeterName = "DiffPdf.Automation";

    private readonly Meter _meter;
    private readonly Histogram<double> _runDuration;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _stepDuration;
    private volatile int _failing;

    public AutomationMetrics()
    {
        _meter = new Meter(MeterName);
        _runDuration = _meter.CreateHistogram<double>(
            "diffpdf.automation.run.duration", unit: "s",
            description: "Wall-clock duration of one automation attempt, tagged by outcome and trigger.");
        _runs = _meter.CreateCounter<long>(
            "diffpdf.automation.runs", unit: "{run}",
            description: "Automation attempts executed, tagged by outcome and trigger.");
        _stepDuration = _meter.CreateHistogram<double>(
            "diffpdf.automation.step.duration", unit: "s",
            description: "Wall-clock duration of one automation step, tagged by step type and outcome.");
        _meter.CreateObservableGauge(
            "diffpdf.automation.failing", () => _failing, unit: "{automation}",
            description: "Enabled automations whose consecutive-failure streak has reached their threshold.");
    }

    /// <summary>Records one executed attempt.</summary>
    public void RecordRun(AutomationRunOutcome outcome, AutomationTriggerKind trigger, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "outcome", Tag(outcome) },
            { "trigger", trigger switch
                {
                    AutomationTriggerKind.Schedule => "schedule",
                    AutomationTriggerKind.Event => "event",
                    _ => "manual",
                } },
        };
        _runs.Add(1, tags);
        if (duration > TimeSpan.Zero)
            _runDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>Records one executed step.</summary>
    public void RecordStep(AutomationStepType type, AutomationRunOutcome outcome, TimeSpan duration) =>
        _stepDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("type", type.ToString()),
            new KeyValuePair<string, object?>("outcome", Tag(outcome)));

    /// <summary>Refreshes the failing-automations gauge (fed by the engine's leading tick).</summary>
    public void SetFailing(int count) => _failing = count;

    private static string Tag(AutomationRunOutcome outcome) => outcome switch
    {
        AutomationRunOutcome.Ok => "ok",
        AutomationRunOutcome.Warning => "warning",
        _ => "failed",
    };

    public void Dispose() => _meter.Dispose();
}
