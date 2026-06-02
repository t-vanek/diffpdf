using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Pdf.Rendering;

/// <summary>Selects a registered renderer by backend.</summary>
public sealed class RendererFactory(IEnumerable<IPdfPageRenderer> renderers) : IPdfPageRendererFactory
{
    private readonly Dictionary<RendererBackend, IPdfPageRenderer> _byBackend =
        renderers.ToDictionary(r => r.Backend);

    public IPdfPageRenderer GetRenderer(RendererBackend backend)
    {
        if (_byBackend.TryGetValue(backend, out var renderer))
            return renderer;

        // Fall back to any available renderer so a missing backend never hard-fails.
        return _byBackend.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No PDF page renderer is registered.");
    }
}
