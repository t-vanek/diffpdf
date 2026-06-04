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
    IPdfWorkLimiter limiter,
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
        using var _ = await limiter.AcquireAsync(ct);
        string tempOut = Path.Combine(Path.GetTempPath(), $"diffpdf_{Guid.NewGuid():N}.png");
        var sw = Stopwatch.StartNew();
        string outcome = "error";
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

            string stderr;
            try
            {
                stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // The render hung: kill the entire Ghostscript process tree so no gs.exe is left orphaned.
                KillTree(process);
                if (ct.IsCancellationRequested) { outcome = "cancelled"; throw; } // outer cancellation — propagate
                outcome = "timeout";
                throw new TimeoutException($"Ghostscript render of page {pageNumber} timed out after {_options.Timeout}.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Ghostscript exited with code {process.ExitCode}: {stderr}");

            byte[] png = await File.ReadAllBytesAsync(tempOut, ct);
            using var bitmap = SKBitmap.Decode(png);

            outcome = "ok";
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
            PdfRenderMetrics.Record(Backend, sw.Elapsed.TotalSeconds, outcome);
            if (File.Exists(tempOut))
            {
                try { File.Delete(tempOut); }
                catch (IOException ex) { logger.LogDebug(ex, "Could not delete temp file {File}", tempOut); }
            }
        }
    }

    /// <summary>Best-effort kill of a timed-out Ghostscript process and its children, with a brief synchronous reap.</summary>
    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to kill timed-out Ghostscript process.");
        }
    }

    /// <summary>Runs <c>{gs} --version</c> (short timeout) to confirm the Ghostscript binary is reachable.</summary>
    public async Task<RendererHealth> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process is null)
                return new RendererHealth(Backend, Available: false, Version: null);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            string stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            string version = stdout.Trim();
            return process.ExitCode == 0
                ? new RendererHealth(Backend, Available: true, Version: string.IsNullOrEmpty(version) ? null : version)
                : new RendererHealth(Backend, Available: false, Version: null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ghostscript availability probe failed.");
            return new RendererHealth(Backend, Available: false, Version: null);
        }
    }
}
