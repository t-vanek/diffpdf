using System.Diagnostics;
using System.Text;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Pdf.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Ad-hoc LOCAL benchmark: times the real comparison engine (wired via <c>AddDiffPdf</c>, PDFium renderer,
/// production-default 150 DPI text+visual) on a single real PDF pair. Point it at data with the
/// <c>DIFFPDF_BENCH_OLD</c> / <c>DIFFPDF_BENCH_NEW</c> environment variables (two folders of matched,
/// same-named PDFs); unset → the test skips, so it is a harmless no-op on CI / other machines. The report is
/// written to %TEMP%/diffpdf-bench.txt.
/// </summary>
public class PdfComparisonBenchmark
{
    [Fact]
    public async Task Time_single_pdf_comparison()
    {
        var sb = new StringBuilder();
        var report = Path.Combine(Path.GetTempPath(), "diffpdf-bench.txt");
        void Log(string s) => sb.AppendLine(s);

        try
        {
            var oldDir = Environment.GetEnvironmentVariable("DIFFPDF_BENCH_OLD");
            var newDir = Environment.GetEnvironmentVariable("DIFFPDF_BENCH_NEW");
            if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(newDir)
                || !Directory.Exists(oldDir) || !Directory.Exists(newDir))
            {
                Log("SKIP: set DIFFPDF_BENCH_OLD and DIFFPDF_BENCH_NEW to two folders of matched PDFs.");
                return;
            }

            // First file present in both folders (a real matched pair).
            var oldNames = Directory.GetFiles(oldDir, "*.pdf").Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pair = Directory.GetFiles(newDir, "*.pdf").Select(Path.GetFileName)
                .FirstOrDefault(n => n is not null && oldNames.Contains(n));
            if (pair is null) { Log("SKIP: no matched old/new pair."); return; }

            string oldPath = Path.Combine(oldDir, pair), newPath = Path.Combine(newDir, pair);
            string artifacts = Path.Combine(Path.GetTempPath(), "diffpdf-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(artifacts);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDiffPdf();
            using var sp = services.BuildServiceProvider();
            var engine = sp.GetRequiredService<IComparisonEngine>();
            // Default backend is Ghostscript (external `gs`); use PDFium (bundled, in-process) so the benchmark
            // runs without an external dependency.
            var options = new ComparisonOptions { Renderer = RendererBackend.Pdfium };

            Log($"Pair: {pair}");
            Log($"old {new FileInfo(oldPath).Length / 1024} KB | new {new FileInfo(newPath).Length / 1024} KB | DPI {options.Dpi}, mode {options.Mode}, renderer {options.Renderer}");

            // Warmup (JIT + native PDFium load) — not counted.
            var warm = Stopwatch.StartNew();
            var first = await engine.CompareAsync(oldPath, newPath, options, artifacts);
            warm.Stop();
            Log($"warmup: {warm.ElapsedMilliseconds} ms — outcome={first.Outcome}, identical={first.AreIdentical}, diffPages={first.DifferingPages}, error={first.Error}");

            var times = new List<long>();
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                await engine.CompareAsync(oldPath, newPath, options, artifacts);
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
            }
            Log($"runs (ms): {string.Join(", ", times)}");
            Log($"min={times.Min()} | avg={times.Average():0} | max={times.Max()} ms  (5 runs)");

            try { Directory.Delete(artifacts, recursive: true); } catch { /* temp */ }
        }
        finally
        {
            File.WriteAllText(report, sb.ToString());
        }
    }
}
