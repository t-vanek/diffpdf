namespace DiffPdf.Messaging.ControlPlane;

/// <summary>Configures the control-plane runner. Only the tick cadence lives here; checks are runtime resources in the DB.</summary>
public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    /// <summary>How often the runner evaluates which checks are due (seconds). Clamped to a sane minimum.</summary>
    public int TickSeconds { get; set; } = 20;
}
