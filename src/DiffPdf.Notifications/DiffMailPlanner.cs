using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>One diff PDF picked for sending: the pair it belongs to plus the flat artifact file name to attach.</summary>
public sealed record DiffMailItem(FilePairResult Pair, string ArtifactFileName)
{
    /// <summary>Unique, human-readable name for the file inside a ZIP — the pair's relative path keeps
    /// same-named files from different subfolders apart (artifact names themselves are flat).</summary>
    public string ZipEntryName => Path.ChangeExtension(Pair.RelativePath, ".diff.pdf");
}

/// <summary>The selection for one send: what to attach, what was asked for but has no diff PDF, and what is unknown.</summary>
public sealed record DiffMailPlan(
    IReadOnlyList<DiffMailItem> Items,
    IReadOnlyList<string> SkippedFiles,
    IReadOnlyList<string> UnknownFiles);

/// <summary>
/// Pure selection + composition logic for "send the differing diff PDFs by e-mail" — no file system, no SMTP —
/// so the endpoint stays a thin binder and the rules are unit-testable. Selection: an explicit file list picks
/// those pairs (whatever their status, e.g. an Error pair that still produced a partial diff); no list means
/// every pair the engine flagged as <see cref="FilePairStatus.Differs"/>. Either way only pairs that actually
/// produced a highlighted diff PDF can be attached — the rest are reported as skipped.
/// </summary>
public static class DiffMailPlanner
{
    /// <summary>Auto-ZIP kicks in above this many attachments (mail clients handle one archive better than dozens of files).</summary>
    public const int ZipFileCountThreshold = 4;

    /// <summary>Auto-ZIP kicks in above this raw payload size; PDFs compress modestly, but one attachment still beats ten.</summary>
    public const long ZipSizeThresholdBytes = 8 * 1024 * 1024;

    public static DiffMailPlan Plan(BatchComparisonReport report, IReadOnlyList<string>? requestedFiles)
    {
        var skipped = new List<string>();
        var items = new List<DiffMailItem>();

        if (requestedFiles is { Count: > 0 })
        {
            var byPath = report.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            foreach (var requested in requestedFiles.Select(f => f?.Trim()).Where(f => !string.IsNullOrEmpty(f)))
            {
                if (!byPath.TryGetValue(requested!, out var pair)) unknown.Add(requested!);
                else if (pair.HighlightedPdfPath is null) skipped.Add(pair.RelativePath);
                else items.Add(new DiffMailItem(pair, Path.GetFileName(pair.HighlightedPdfPath)));
            }
            return new DiffMailPlan(items, skipped, unknown);
        }

        foreach (var pair in report.Files.Where(f => f.Status == FilePairStatus.Differs))
        {
            if (pair.HighlightedPdfPath is null) skipped.Add(pair.RelativePath);
            else items.Add(new DiffMailItem(pair, Path.GetFileName(pair.HighlightedPdfPath)));
        }
        return new DiffMailPlan(items, skipped, []);
    }

    /// <summary>ZIP when the caller asks for it, or (left to "auto") when there are many/large attachments.</summary>
    public static bool ShouldZip(bool? zipOverride, int fileCount, long totalBytes) =>
        zipOverride ?? (fileCount > ZipFileCountThreshold || totalBytes > ZipSizeThresholdBytes);

    /// <summary>Plain-text mail body: scope + verdict counts, the attached pairs with their metrics, the sender's
    /// note and a deep link to the job (when a public BaseUrl is configured).</summary>
    public static string ComposeBody(
        ComparisonJob job, DiffMailPlan plan, string? note, bool zipped, string? jobLink)
    {
        var report = job.Report!;
        var lines = new List<string>
        {
            $"Větev/instance: {job.BranchKey}/{job.InstanceKey}",
            $"Dokončeno: {report.CompletedAt.ToLocalTime():dd.MM.yyyy HH:mm}",
            $"Výsledek dávky: {report.Differing} odlišných z {report.Total} (chyby: {report.Errors}, jen ve staré: {report.OnlyInOld}, jen v nové: {report.OnlyInNew})",
            "",
        };

        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add(note.Trim());
            lines.Add("");
        }

        lines.Add(zipped
            ? $"V příloze je ZIP s {plan.Items.Count} zvýrazněnými diff PDF (stará verze vlevo, nová vpravo):"
            : $"V příloze {(plan.Items.Count == 1 ? "je zvýrazněné diff PDF" : $"jsou {plan.Items.Count} zvýrazněná diff PDF")} (stará verze vlevo, nová vpravo):");
        foreach (var item in plan.Items)
            lines.Add($"  • {item.Pair.RelativePath} — shoda {item.Pair.Similarity:P1}, odlišných stránek: {item.Pair.DifferingPages}");

        if (plan.SkippedFiles.Count > 0)
        {
            lines.Add("");
            lines.Add($"Bez diff PDF (nelze přiložit): {string.Join(", ", plan.SkippedFiles)}");
        }

        if (jobLink is not null)
        {
            lines.Add("");
            lines.Add($"Detail úlohy: {jobLink}");
        }

        lines.Add("");
        lines.Add($"— DiffPdf, úloha {job.Id}");
        return string.Join('\n', lines);
    }
}
