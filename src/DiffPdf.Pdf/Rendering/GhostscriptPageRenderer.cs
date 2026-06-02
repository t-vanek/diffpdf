using System.Diagnostics;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using UglyToad.PdfPig;

namespace DiffPdf.Pdf.Rendering;

/// <summary>
/// Renders pages by shelling out to the Ghostscript CLI (AGPL — see README).
/// Produces high-fidelity raster output and is the default backend.
/// </summary>
public sealed class GhostscriptPageRenderer(
    IOptions<GhostscriptOptions> options,
    ILogger<GhostscriptPageRenderer> logger) : IPdfPageRenderer
{
    private readonly GhostscriptOptions _options = options.Value;

    public RendererBackend Backend => RendererBackend.Ghostscript;

    public int GetPageCount(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.NumberOfPages;
    }

    public async Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default)
    {
        string tempOut = Path.Combine(Path.GetTempPath(), $"diffpdf_{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-dQUIET");
            psi.ArgumentList.Add("-dBATCH");
            psi.ArgumentList.Add("-dNOPAUSE");
            psi.ArgumentList.Add("-dSAFER");
            psi.ArgumentList.Add("-sDEVICE=png16m");
            psi.ArgumentList.Add($"-r{dpi}");
            psi.ArgumentList.Add($"-dFirstPage={pageNumber}");
            psi.ArgumentList.Add($"-dLastPage={pageNumber}");
            psi.ArgumentList.Add($"-sOutputFile={tempOut}");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Ghostscript process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            string stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Ghostscript exited with code {process.ExitCode}: {stderr}");

            byte[] png = await File.ReadAllBytesAsync(tempOut, ct);
            using var bitmap = SKBitmap.Decode(png);

            return new RenderedPage
            {
                PageNumber = pageNumber,
                Dpi = dpi,
                WidthPx = bitmap?.Width ?? 0,
                HeightPx = bitmap?.Height ?? 0,
                Png = png,
            };
        }
        finally
        {
            if (File.Exists(tempOut))
            {
                try { File.Delete(tempOut); }
                catch (IOException ex) { logger.LogDebug(ex, "Could not delete temp file {File}", tempOut); }
            }
        }
    }
}
