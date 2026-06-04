using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Reusable editor model for <see cref="ComparisonOptions"/> (used by Single compare).</summary>
public partial class ComparisonOptionsViewModel : ObservableObject
{
    public ComparisonMode[] Modes { get; } = Enum.GetValues<ComparisonMode>();
    public Strictness[] Strictnesses { get; } = Enum.GetValues<Strictness>();
    public HighlightLayout[] Layouts { get; } = Enum.GetValues<HighlightLayout>();
    public HighlightStyle[] Styles { get; } = Enum.GetValues<HighlightStyle>();
    public RendererBackend[] Renderers { get; } = Enum.GetValues<RendererBackend>();

    [ObservableProperty] private ComparisonMode _mode = ComparisonMode.Both;
    [ObservableProperty] private decimal _dpi = 150;
    [ObservableProperty] private Strictness _strictness = Strictness.Balanced;
    [ObservableProperty] private decimal? _shiftTolerance;
    [ObservableProperty] private decimal? _visualThreshold;
    [ObservableProperty] private decimal? _textDifferenceThreshold;
    [ObservableProperty] private decimal? _pagesFrom;
    [ObservableProperty] private decimal? _pagesTo;
    [ObservableProperty] private bool _normalizeWhitespace = true;
    [ObservableProperty] private bool _alignPages = true;
    [ObservableProperty] private bool _detectBlankPages = true;
    [ObservableProperty] private bool _detectContentErrors = true;
    [ObservableProperty] private bool _produceHighlightedPdf = true;
    [ObservableProperty] private HighlightLayout _highlightLayout = HighlightLayout.SideBySide;
    [ObservableProperty] private HighlightStyle _highlightStyle = HighlightStyle.Raster;
    [ObservableProperty] private RendererBackend _renderer = RendererBackend.Ghostscript;
    [ObservableProperty] private string _ignoreTextPatterns = string.Empty;
    [ObservableProperty] private string _contentErrorPatterns = string.Empty;

    public ComparisonOptions ToOptions() => new()
    {
        Mode = Mode,
        Dpi = (int)Dpi,
        Strictness = Strictness,
        ShiftTolerance = ShiftTolerance is { } s ? (int)s : null,
        VisualThreshold = VisualThreshold is { } v ? (double)v : null,
        TextDifferenceThreshold = TextDifferenceThreshold is { } t ? (double)t : null,
        Pages = new PageRange(PagesFrom is { } f ? (int)f : null, PagesTo is { } pt ? (int)pt : null),
        NormalizeWhitespace = NormalizeWhitespace,
        AlignPages = AlignPages,
        DetectBlankPages = DetectBlankPages,
        DetectContentErrors = DetectContentErrors,
        ProduceHighlightedPdf = ProduceHighlightedPdf,
        HighlightLayout = HighlightLayout,
        HighlightStyle = HighlightStyle,
        Renderer = Renderer,
        IgnoreTextPatterns = Split(IgnoreTextPatterns),
        ContentErrorPatterns = string.IsNullOrWhiteSpace(ContentErrorPatterns) ? null : Split(ContentErrorPatterns),
    };

    public void Load(ComparisonOptions o)
    {
        Mode = o.Mode;
        Dpi = o.Dpi;
        Strictness = o.Strictness;
        ShiftTolerance = o.ShiftTolerance;
        VisualThreshold = o.VisualThreshold is { } v ? (decimal)v : null;
        TextDifferenceThreshold = o.TextDifferenceThreshold is { } t ? (decimal)t : null;
        PagesFrom = o.Pages.From;
        PagesTo = o.Pages.To;
        NormalizeWhitespace = o.NormalizeWhitespace;
        AlignPages = o.AlignPages;
        DetectBlankPages = o.DetectBlankPages;
        DetectContentErrors = o.DetectContentErrors;
        ProduceHighlightedPdf = o.ProduceHighlightedPdf;
        HighlightLayout = o.HighlightLayout;
        HighlightStyle = o.HighlightStyle;
        Renderer = o.Renderer;
        IgnoreTextPatterns = string.Join(", ", o.IgnoreTextPatterns);
        ContentErrorPatterns = o.ContentErrorPatterns is null ? string.Empty : string.Join(", ", o.ContentErrorPatterns);
    }

    private static IReadOnlyList<string> Split(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
