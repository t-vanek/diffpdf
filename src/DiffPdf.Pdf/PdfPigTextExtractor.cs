using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DiffPdf.Pdf;

/// <summary>Positioned text extraction backed by PdfPig (Apache 2.0).</summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public int GetPageCount(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.NumberOfPages;
    }

    public Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default)
    {
        var pages = new List<PageText>();
        using var doc = PdfDocument.Open(path);

        foreach (Page page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (!range.Contains(page.Number)) continue;

            var words = page.GetWords()
                .Select(w => new PositionedWord(
                    w.Text,
                    new RectangleD(
                        w.BoundingBox.Left,
                        w.BoundingBox.Bottom,
                        w.BoundingBox.Width,
                        w.BoundingBox.Height)))
                .ToList();

            pages.Add(new PageText
            {
                PageNumber = page.Number,
                Width = page.Width,
                Height = page.Height,
                Words = words,
            });
        }

        return Task.FromResult<IReadOnlyList<PageText>>(pages);
    }
}
