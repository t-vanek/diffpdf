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
            pages.Add(ExtractPage(page));
        }

        return Task.FromResult<IReadOnlyList<PageText>>(pages);
    }

    /// <summary>
    /// One document open and one page pass cover both the probe (status + geometry) and the extraction —
    /// the split <see cref="Probe"/> + <see cref="ExtractAsync"/> path opens and walks the document twice.
    /// PdfPig is synchronous, so the work runs on the thread pool; the engine extracts old and new
    /// concurrently this way.
    /// </summary>
    public Task<DocumentExtraction> ProbeAndExtractAsync(string path, PageRange range, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(path))
                return new DocumentExtraction(DocumentInfo.Failed(DocumentStatus.NotFound, $"File not found: {path}"), []);

            try
            {
                using var doc = PdfDocument.Open(path);
                int count = doc.NumberOfPages;
                if (count == 0)
                    return new DocumentExtraction(new DocumentInfo { Status = DocumentStatus.Empty, PageCount = 0 }, []);

                var geometry = new List<PageGeometry>(count);
                var pages = new List<PageText>();
                foreach (Page page in doc.GetPages())
                {
                    ct.ThrowIfCancellationRequested();
                    geometry.Add(new PageGeometry(page.Number, page.Width, page.Height, page.Rotation.Value));
                    if (!range.Contains(page.Number)) continue;
                    pages.Add(ExtractPage(page));
                }

                var info = new DocumentInfo { Status = DocumentStatus.Ok, PageCount = count, Pages = geometry };
                return new DocumentExtraction(info, pages);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var status = LooksEncrypted(ex) ? DocumentStatus.Encrypted : DocumentStatus.Unreadable;
                return new DocumentExtraction(DocumentInfo.Failed(status, ex.Message), []);
            }
        }, ct);

    private static PageText ExtractPage(Page page)
    {
        List<PositionedWord> words;
        bool extractionFailed = false;
        try
        {
            words = page.GetWords()
                .Select(w => new PositionedWord(
                    w.Text,
                    new RectangleD(w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Width, w.BoundingBox.Height)))
                .ToList();
        }
        catch (Exception) // one bad page (e.g. a corrupt embedded font) must not abort the whole document
        {
            words = [];
            extractionFailed = true;
        }

        return new PageText
        {
            PageNumber = page.Number,
            Width = page.Width,
            Height = page.Height,
            Rotation = page.Rotation.Value,
            Words = words,
            ExtractionFailed = extractionFailed,
        };
    }

    private static bool LooksEncrypted(Exception ex)
    {
        string m = ex.Message;
        return ex.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase)
            || m.Contains("password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase);
    }
}
