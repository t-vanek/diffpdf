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
    /// <summary>Success / identical / passed.</summary>
    public static IBrush Good { get; } = Brush("#6FCF73");

    /// <summary>Failure / differing / error / not-passed.</summary>
    public static IBrush Bad { get; } = Brush("#E06C75");

    /// <summary>Attention: waiting / queued / only-in-one-side / warning.</summary>
    public static IBrush Warning { get; } = Brush("#D8A657");

    /// <summary>Running / active / informational.</summary>
    public static IBrush Info { get; } = Brush("#9CDCFE");

    /// <summary>Paused.</summary>
    public static IBrush Paused { get; } = Brush("#E5A050");

    /// <summary>Skipped / cancelled.</summary>
    public static IBrush Skipped { get; } = Brush("#9A9A9A");

    /// <summary>Secondary / inactive text.</summary>
    public static IBrush Muted { get; } = Brush("#7A7A7A");

    /// <summary>Dimmest — a zero / "none" value that should recede.</summary>
    public static IBrush Faint { get; } = Brush("#5A5A5A");

    /// <summary>Default body text on the dark background.</summary>
    public static IBrush Text { get; } = Brush("#CCCCCC");

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
