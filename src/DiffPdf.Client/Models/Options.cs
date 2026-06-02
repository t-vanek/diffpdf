namespace DiffPdf.Client;

/// <summary>A rectangle (top-left origin) used by <see cref="IgnoreRegion"/>.</summary>
public sealed record RectangleD(double X, double Y, double Width, double Height);

/// <summary>1-based inclusive page range; null bounds mean "open ended".</summary>
public readonly record struct PageRange(int? From = null, int? To = null);

/// <summary>A rectangular area excluded from comparison (e.g. a footer with a timestamp).</summary>
public sealed record IgnoreRegion
{
    public required RectangleD Area { get; init; }
    public IgnoreUnit Unit { get; init; } = IgnoreUnit.Fraction;
    /// <summary>Page numbers this region applies to; null = every page.</summary>
    public IReadOnlyList<int>? Pages { get; init; }
    public string? Label { get; init; }
}

/// <summary>Pass/fail thresholds for a batch run (CI gate). A null limit means "no limit".</summary>
public sealed record BatchGate
{
    public bool FailOnAnyDifference { get; init; }
    public int? MaxDifferingFiles { get; init; }
    public int? MaxErrors { get; init; }
    public int? MaxFilesWithContentErrors { get; init; }
}

/// <summary>Credentials for an authenticated SMB/CIFS share.</summary>
public sealed record NetworkCredentials
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? Domain { get; init; }
}

/// <summary>Tunable knobs for a comparison run (mirrors the server's options).</summary>
public sealed record ComparisonOptions
{
    public ComparisonMode Mode { get; init; } = ComparisonMode.Both;
    public PageRange Pages { get; init; } = new();
    public int Dpi { get; init; } = 150;
    public Strictness Strictness { get; init; } = Strictness.Balanced;

    /// <summary>Per-pixel channel tolerance (0-255); null = derived from <see cref="Strictness"/>.</summary>
    public byte? PixelTolerance { get; init; }
    /// <summary>Fraction of differing pixels (0-1) tolerated per page; null = preset.</summary>
    public double? VisualThreshold { get; init; }
    /// <summary>Fraction of changed words (0-1) tolerated per page; null = preset.</summary>
    public double? TextDifferenceThreshold { get; init; }
    /// <summary>Radius (px) to absorb sub-pixel/AA shifts; null = preset.</summary>
    public int? ShiftTolerance { get; init; }

    /// <summary>Grid cell size (px) for clustering differing pixels into highlight regions.</summary>
    public int VisualClusterCellSize { get; init; } = 24;

    public bool NormalizeWhitespace { get; init; } = true;
    public bool AlignPages { get; init; } = true;
    public double PageMatchThreshold { get; init; } = 0.2;

    public bool DetectBlankPages { get; init; } = true;
    public double BlankPageThreshold { get; init; } = 0.0002;

    public bool DetectContentErrors { get; init; } = true;
    public IReadOnlyList<string>? ContentErrorPatterns { get; init; }

    public IReadOnlyList<IgnoreRegion> IgnoreRegions { get; init; } = [];
    public IReadOnlyList<string> IgnoreTextPatterns { get; init; } = [];

    public bool ProduceHighlightedPdf { get; init; } = true;
    public HighlightLayout HighlightLayout { get; init; } = HighlightLayout.SideBySide;
    public HighlightStyle HighlightStyle { get; init; } = HighlightStyle.Raster;
    public RendererBackend Renderer { get; init; } = RendererBackend.Ghostscript;
}
