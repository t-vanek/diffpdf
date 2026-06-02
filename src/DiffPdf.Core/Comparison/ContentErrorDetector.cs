using System.Text.RegularExpressions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Scans the extracted text of each page for known error messages that a
/// reporting engine may have rendered into the PDF (e.g. "subreport error").
/// </summary>
public sealed class ContentErrorDetector : IContentErrorDetector
{
    private const int SnippetRadius = 40;

    public IReadOnlyList<ContentError> Detect(
        IReadOnlyList<PageText> pages,
        ContentErrorSide side,
        ComparisonOptions options)
    {
        if (!options.DetectContentErrors || options.ContentErrorPatterns.Count == 0)
            return [];

        var regexes = options.ContentErrorPatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        var findings = new List<ContentError>();
        foreach (var page in pages)
        {
            if (!page.HasText) continue;
            string text = string.Join(' ', page.Words.Select(w => w.Text));

            foreach (var (regex, pattern) in regexes.Zip(options.ContentErrorPatterns))
            {
                var match = regex.Match(text);
                if (match.Success)
                    findings.Add(new ContentError(side, page.PageNumber, pattern, Snippet(text, match.Index, match.Length)));
            }
        }

        return findings;
    }

    private static string Snippet(string text, int index, int length)
    {
        int start = Math.Max(0, index - SnippetRadius);
        int end = Math.Min(text.Length, index + length + SnippetRadius);
        string s = text[start..end].Trim();
        return (start > 0 ? "…" : "") + s + (end < text.Length ? "…" : "");
    }
}
