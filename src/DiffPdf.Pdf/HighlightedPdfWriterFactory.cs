using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Pdf;

/// <summary>Selects a highlighted-PDF writer by style.</summary>
public sealed class HighlightedPdfWriterFactory(IEnumerable<IHighlightedPdfWriter> writers) : IHighlightedPdfWriterFactory
{
    private readonly Dictionary<HighlightStyle, IHighlightedPdfWriter> _byStyle = writers.ToDictionary(w => w.Style);

    public IHighlightedPdfWriter Get(HighlightStyle style) =>
        _byStyle.TryGetValue(style, out var writer)
            ? writer
            : _byStyle.Values.First();
}
