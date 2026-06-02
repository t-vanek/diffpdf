using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DiffPdf.Pdf;

/// <summary>Positioned text extraction + readability probing backed by PdfPig (Apache 2.0).</summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public DocumentInfo Probe(string path)
    {
        if (!File.Exists(path))
            return DocumentInfo.Failed(DocumentStatus.NotFound, $"File not found: {path}");

        try
        {
            using var doc = PdfDocument.Open(path);
            int count = doc.NumberOfPages;
            if (count == 0)
                return new DocumentInfo { Status = DocumentStatus.Empty, PageCount = 0 };

            var geometry = doc.GetPages()
                .Select(p => new PageGeometry(p.Number, p.Width, p.Height, p.Rotation.Value))
                .ToList();

            return new DocumentInfo { Status = DocumentStatus.Ok, PageCount = count, Pages = geometry };
        }
        catch (Exception ex)
        {
            var status = LooksEncrypted(ex) ? DocumentStatus.Encrypted : DocumentStatus.Unreadable;
            return DocumentInfo.Failed(status, ex.Message);
        }
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
                    new RectangleD(w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Width, w.BoundingBox.Height)))
                .ToList();

            pages.Add(new PageText
            {
                PageNumber = page.Number,
                Width = page.Width,
                Height = page.Height,
                Rotation = page.Rotation.Value,
                Words = words,
            });
        }

        return Task.FromResult<IReadOnlyList<PageText>>(pages);
    }

    private static bool LooksEncrypted(Exception ex)
    {
        string m = ex.Message;
        return ex.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase)
            || m.Contains("password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase);
    }
}
