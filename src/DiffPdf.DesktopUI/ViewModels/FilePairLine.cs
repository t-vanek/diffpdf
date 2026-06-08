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
        // Append the engine-only compare time for pairs that were actually compared.
        if (r.CompareMs is { } ms && r.Status is FilePairStatus.Identical or FilePairStatus.Differs)
            detail = detail.Length == 0 ? FormatMs(ms) : $"{detail} · {FormatMs(ms)}";
        return new FilePairLine(r.RelativePath, icon, text, brush, detail, differing, r);
    }

    private static string FormatMs(long ms) => ms >= 1000 ? $"{ms / 1000.0:0.0} s" : $"{ms} ms";

    public static FilePairLine FromTask(FilePairTaskSummary t)
    {
        var (icon, text, brush) = t.Status switch
        {
            "Completed" => ("✓", "Hotovo", Palette.Good),
            "Running" => ("▶", "Běží", Palette.Info),
            "Failed" => ("✗", "Chyba", Palette.Bad),
            "Paused" => ("⏸", "Pozastaveno", Palette.Paused),
            _ => ("⏳", "Čeká", Palette.Muted),
        };
        string detail = t.AttemptCount > 1 ? $"{t.AttemptCount}. pokus" : t.ResultStatus ?? "";
        bool differing = t.ResultStatus is "Differs" or "OnlyInOld" or "OnlyInNew" or "Error";
        return new FilePairLine(t.RelativePath, icon, text, brush, detail, differing, null);
    }

    private static (string Icon, string Text, IBrush Brush, bool Differing) Classify(FilePairStatus s) => s switch
    {
        FilePairStatus.Identical => ("✓", "Shodné", Palette.Good, false),
        FilePairStatus.Differs => ("✗", "Odlišné", Palette.Bad, true),
        FilePairStatus.OnlyInOld => ("⤳", "Jen ve staré", Palette.Warning, true),
        FilePairStatus.OnlyInNew => ("⤳", "Jen v nové", Palette.Warning, true),
        FilePairStatus.Error => ("⚠", "Chyba", Palette.Bad, true),
        _ => ("•", s.ToString(), Palette.Muted, false),
    };
}
