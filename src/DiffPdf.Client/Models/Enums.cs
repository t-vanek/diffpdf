namespace DiffPdf.Client;

/// <summary>Lifecycle state of a batch comparison job.</summary>
public enum JobStatus
{
    Draft,
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Classification of a single file pair within a batch.</summary>
public enum FilePairStatus
{
    Identical,
    Differs,
    OnlyInOld,
    OnlyInNew,
    Error,
}

/// <summary>State of one required subfolder (old/new/reports) of an instance.</summary>
public enum StructureItemState
{
    Present,
    Missing,
    WrongType,
    Created,
    Repaired,
}

/// <summary>Which comparison strategy (or strategies) to run.</summary>
[Flags]
public enum ComparisonMode
{
    None = 0,
    Text = 1,
    Visual = 2,
    Both = Text | Visual,
}

/// <summary>Engine strictness presets, from pixel-perfect to forgiving.</summary>
public enum Strictness
{
    Exact,
    Strict,
    Balanced,
    Lenient,
}

/// <summary>Layout of the highlighted diff PDF.</summary>
public enum HighlightLayout
{
    SideBySide,
    Single,
}

/// <summary>How the highlighted diff PDF is rendered.</summary>
public enum HighlightStyle
{
    Raster,
    VectorOverlay,
}

/// <summary>Renderer backend for the visual comparison.</summary>
public enum RendererBackend
{
    Ghostscript,
    Pdfium,
}

/// <summary>Unit in which an ignore-region rectangle is expressed.</summary>
public enum IgnoreUnit
{
    Fraction,
    Points,
}

/// <summary>The kind of batch outcome a notification subscription can fire on.</summary>
public enum NotificationEvent
{
    Completed,
    GateViolated,
    Failed,
}
