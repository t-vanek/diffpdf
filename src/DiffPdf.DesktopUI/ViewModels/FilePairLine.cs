using Avalonia.Media;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One row of a job's unified file-pair list: the file, a coloured status, a short detail (similarity + page
/// delta when complete, attempt/state while running) and — when the run produced a highlighted diff — the
/// <see cref="Diff"/> result to download. Built from a <see cref="FilePairResult"/> (finished) or a
/// <see cref="FilePairTaskSummary"/> (in flight).
/// </summary>
public sealed record FilePairLine(string Name, string Icon, string StatusText, IBrush Brush, string Detail, bool IsDiffering, FilePairResult? Diff)
{
    public bool HasDiff => Diff?.HighlightedPdfPath is not null;

    public static FilePairLine FromResult(FilePairResult r)
    {
        var (icon, text, brush, differing) = Classify(r.Status);
        string detail = r.Status switch
        {
            FilePairStatus.Differs => $"{r.Similarity:P0} · {r.DifferingPages} str.",
            FilePairStatus.Error => r.Error ?? "chyba",
            _ => "",
        };
        return new FilePairLine(r.RelativePath, icon, text, brush, detail, differing, r);
    }

    public static FilePairLine FromTask(FilePairTaskSummary t)
    {
        var (icon, text, brush) = t.Status switch
        {
            "Completed" => ("✓", "Hotovo", Green),
            "Running" => ("▶", "Běží", Blue),
            "Failed" => ("✗", "Chyba", Red),
            "Paused" => ("⏸", "Pozastaveno", Amber),
            _ => ("⏳", "Čeká", Muted),
        };
        string detail = t.AttemptCount > 1 ? $"{t.AttemptCount}. pokus" : t.ResultStatus ?? "";
        bool differing = t.ResultStatus is "Differs" or "OnlyInOld" or "OnlyInNew" or "Error";
        return new FilePairLine(t.RelativePath, icon, text, brush, detail, differing, null);
    }

    private static (string Icon, string Text, IBrush Brush, bool Differing) Classify(FilePairStatus s) => s switch
    {
        FilePairStatus.Identical => ("✓", "Shodné", Green, false),
        FilePairStatus.Differs => ("✗", "Odlišné", Red, true),
        FilePairStatus.OnlyInOld => ("⤳", "Jen ve staré", Amber, true),
        FilePairStatus.OnlyInNew => ("⤳", "Jen v nové", Amber, true),
        FilePairStatus.Error => ("⚠", "Chyba", Red, true),
        _ => ("•", s.ToString(), Muted, false),
    };

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#6FCF73"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E06C75"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#D8A657"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#7A7A7A"));
}
