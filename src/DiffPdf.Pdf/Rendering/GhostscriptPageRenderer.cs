using System.Collections.Concurrent;
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
/// <para>
/// Ghostscript re-parses the whole document on every spawn, so rendering page-by-page costs
/// O(pages × document size). Pages are therefore rendered in blocks of
/// <see cref="GhostscriptOptions.RenderBlockSize"/>: one spawn renders the requested page plus its
/// neighbours, and the byproducts wait in a bounded <see cref="RenderedPageCache"/> until the engine asks
/// for them. Any cache or block miss falls back to the original single-page spawn, so block rendering can
/// only add speed, never change behaviour.
/// </para>
/// </summary>
public sealed class GhostscriptPageRenderer(
    IOptions<GhostscriptOptions> options,
    IPdfWorkLimiter limiter,
    ILogger<GhostscriptPageRenderer> logger) : IPdfPageRenderer
{
    private readonly GhostscriptOptions _options = options.Value;
    private readonly int _blockSize = Math.Clamp(options.Value.RenderBlockSize, 1, 32);
    private readonly RenderedPageCache _renderCache = new(options.Value.RenderCacheBytes);
    private readonly ConcurrentDictionary<(string Path, int Dpi, int Block), Lazy<Task>> _inflightBlocks = new();

    public RendererBackend Backend => RendererBackend.Ghostscript;

    public int GetPageCount(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.NumberOfPages;
    }

    public async Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default)
    {
        if (_blockSize <= 1 || _options.RenderCacheBytes <= 0)
            return await RenderSinglePageAsync(path, pageNumber, dpi, ct);

        if (FromCache(path, pageNumber, dpi) is { } cached) return cached;

        // Single-flight per block: concurrently compared pages of the same document await one shared spawn
        // instead of each rendering the block again.
        int block = (pageNumber - 1) / _blockSize;
        var flight = _inflightBlocks.GetOrAdd((path, dpi, block), key => new Lazy<Task>(() => RenderBlockAsync(key)));
        try
        {
            await flight.Value.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The shared block render failed (timeout, corrupt document, gs quirk). Fall through: the
            // single-page render below reproduces a precise per-page error — or succeeds where the block didn't.
        }

        return FromCache(path, pageNumber, dpi) ?? await RenderSinglePageAsync(path, pageNumber, dpi, ct);
    }

    private RenderedPage? FromCache(string path, int pageNumber, int dpi)
    {
        byte[]? png = _renderCache.TryGet(path, dpi, pageNumber);
        if (png is null) return null;

        var info = SKBitmap.DecodeBounds(png);
        if (info.IsEmpty) return null; // undecodable byproduct — let the single-page path fail loudly

        return new RenderedPage
        {
            PageNumber = pageNumber,
            Dpi = dpi,
            WidthPx = info.Width,
            HeightPx = info.Height,
            Png = png,
        };
    }

    /// <summary>
    /// Renders one block of pages and parks every produced page in the cache. Runs once per block
    /// (single-flight) and is deliberately uncancellable — several pages may await the shared result — but a
    /// timeout scaled to the block size still bounds a wedged Ghostscript. Throwing is fine: awaiters fall
    /// back to the single-page render.
    /// </summary>
    private async Task RenderBlockAsync((string Path, int Dpi, int Block) key)
    {
        try
        {
            int firstPage = key.Block * _blockSize + 1;
            int lastPage = firstPage + _blockSize - 1;
            string prefix = Path.Combine(Path.GetTempPath(), $"diffpdf_{Guid.NewGuid():N}");

            using var slot = await limiter.AcquireAsync(CancellationToken.None);

            var sw = Stopwatch.StartNew();
            string outcome = "error";
            int stored = 0;
            try
            {
                // Stamp BEFORE the render: if the input is replaced mid-render, the stale stamp makes every
                // cached byproduct fail its validation, forcing a fresh render instead of serving old content.
                var stampInfo = new FileInfo(key.Path);
                long stampLength = stampInfo.Length;
                DateTime stampWrite = stampInfo.LastWriteTimeUtc;

                using var process = Process.Start(BuildRenderStartInfo(key.Path, firstPage, lastPage, key.Dpi, prefix + "_%03d.png"))
                    ?? throw new InvalidOperationException("Failed to start Ghostscript process.");

                TimeSpan timeout = _options.Timeout * _blockSize;
                using var timeoutCts = new CancellationTokenSource(timeout);
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
                    outcome = "timeout";
                    throw new TimeoutException($"Ghostscript render of pages {firstPage}-{lastPage} timed out after {timeout}.");
                }

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Ghostscript exited with code {process.ExitCode}: {stderr}");

                foreach (var (page, file) in ProducedPages(prefix, firstPage))
                {
                    _renderCache.Store(key.Path, key.Dpi, page, await File.ReadAllBytesAsync(file), stampLength, stampWrite);
                    stored++;
                }

                if (stored == 0)
                {
                    // gs accepted the command but produced nothing this code recognizes — surface it; every page
                    // of the block will fall back to a single-page render, which preserves behaviour at the
                    // pre-block cost.
                    logger.LogWarning(
                        "Ghostscript block render of pages {First}-{Last} of {Path} produced no usable output; pages render individually.",
                        firstPage, lastPage, key.Path);
                }

                outcome = "ok";
            }
            finally
            {
                // Amortized per produced page so the histogram keeps its per-page semantics; a failed or empty
                // block records once with its full duration.
                if (stored > 0)
                {
                    for (int i = 0; i < stored; i++)
                        PdfRenderMetrics.Record(Backend, sw.Elapsed.TotalSeconds / stored, outcome);
                }
                else
                {
                    PdfRenderMetrics.Record(Backend, sw.Elapsed.TotalSeconds, outcome);
                }
                CleanupBlockFiles(prefix);
            }
        }
        finally
        {
            _inflightBlocks.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Maps the files a block render produced to document pages by suffix ORDER, not value: gs numbers
    /// <c>%d</c> by output sequence (1, 2, …) regardless of <c>-dFirstPage</c>, and ordered mapping stays
    /// correct even for a build that numbers by document page instead. A truncated tail (the document ended
    /// before the block) maps correctly either way; files without a numeric suffix are ignored.
    /// </summary>
    internal static IReadOnlyList<(int Page, string File)> ProducedPages(string prefix, int firstPage)
    {
        string directory = Path.GetDirectoryName(prefix)!;
        string namePrefix = Path.GetFileName(prefix) + "_";

        var files = new List<(int Suffix, string File)>();
        foreach (string file in Directory.EnumerateFiles(directory, namePrefix + "*.png"))
        {
            string suffix = Path.GetFileNameWithoutExtension(file)[namePrefix.Length..];
            if (int.TryParse(suffix, out int n)) files.Add((n, file));
        }

        files.Sort((a, b) => a.Suffix.CompareTo(b.Suffix));
        return [.. files.Select((f, i) => (firstPage + i, f.File))];
    }

    private void CleanupBlockFiles(string prefix)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + "_*.png"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete temp render files {Prefix}", prefix);
        }
    }

    private async Task<RenderedPage> RenderSinglePageAsync(string path, int pageNumber, int dpi, CancellationToken ct)
    {
        using var _ = await limiter.AcquireAsync(ct);
        string tempOut = Path.Combine(Path.GetTempPath(), $"diffpdf_{Guid.NewGuid():N}.png");
        var sw = Stopwatch.StartNew();
        string outcome = "error";
        try
        {
            using var process = Process.Start(BuildRenderStartInfo(path, pageNumber, pageNumber, dpi, tempOut))
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
            // Header-only parse: validates Ghostscript produced a decodable PNG and yields the dimensions
            // without paying a full pixel decode here — the comparer / blank detector decode the pixels anyway.
            var info = SKBitmap.DecodeBounds(png);
            if (info.IsEmpty)
            {
                // An unparsable header means Ghostscript produced garbage/empty output — fail loudly instead of
                // returning a 0×0 page that would silently corrupt the visual diff / blank detection. The engine
                // contains it per-page.
                outcome = "error";
                throw new InvalidOperationException($"Ghostscript produced an undecodable PNG for page {pageNumber}.");
            }

            outcome = "ok";
            return new RenderedPage
            {
                PageNumber = pageNumber,
                Dpi = dpi,
                WidthPx = info.Width,
                HeightPx = info.Height,
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

    private ProcessStartInfo BuildRenderStartInfo(string path, int firstPage, int lastPage, int dpi, string outputFile)
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
        psi.ArgumentList.Add($"-dFirstPage={firstPage}");
        psi.ArgumentList.Add($"-dLastPage={lastPage}");
        psi.ArgumentList.Add($"-sOutputFile={outputFile}");
        psi.ArgumentList.Add(path);
        return psi;
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
