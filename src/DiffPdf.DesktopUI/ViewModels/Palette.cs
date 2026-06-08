using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// The single source of truth for the desktop UI's semantic status colours. Previously these were re-declared
/// as <c>new SolidColorBrush(Color.Parse("#…"))</c> in StatLine, JobRowViewModel, FilePairLine and ReadinessView
/// (with subtle drift between them). View-models reference <c>Palette.Good</c> etc.; XAML can use
/// <c>{x:Static vm:Palette.Good}</c>. Brushes are immutable, so a single instance is shared safely.
/// </summary>
public static class Palette
{
    // NOTE: these hexes mirror the XAML design tokens in Theme/Tokens.axaml. Keep both in sync.

    /// <summary>Success / identical / passed.</summary>
    public static IBrush Good { get; } = Brush("#5FD27E");

    /// <summary>Failure / differing / error / not-passed.</summary>
    public static IBrush Bad { get; } = Brush("#FF5C61");

    /// <summary>Attention: waiting / queued / only-in-one-side / warning.</summary>
    public static IBrush Warning { get; } = Brush("#E2A03B");

    /// <summary>Running / active / informational.</summary>
    public static IBrush Info { get; } = Brush("#6FB7FF");

    /// <summary>Paused.</summary>
    public static IBrush Paused { get; } = Brush("#E5A050");

    /// <summary>Skipped / cancelled.</summary>
    public static IBrush Skipped { get; } = Brush("#9A9AA2");

    /// <summary>Secondary / inactive text. (InkDim token — ≥ WCAG AA on the dark surface.)</summary>
    public static IBrush Muted { get; } = Brush("#A9A9B2");

    /// <summary>Dimmest — a zero / "none" value that should recede. (InkFaint token.)</summary>
    public static IBrush Faint { get; } = Brush("#8A8A93");

    /// <summary>Default body text on the dark background. (Ink token.)</summary>
    public static IBrush Text { get; } = Brush("#ECECEC");

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
