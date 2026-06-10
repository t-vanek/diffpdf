using DiffPdf.Pdf.Rendering;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Contract of <see cref="RenderedPageCache"/>: identity caching keyed by (path, dpi, page), invalidation
/// when the source file's stamp changes, LRU eviction under the byte budget, and the disabled mode.
/// </summary>
public sealed class RenderedPageCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diffpdf-rendercache-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private (string Path, long Length, DateTime LastWriteUtc) WriteSource(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        var info = new FileInfo(path);
        return (path, info.Length, info.LastWriteTimeUtc);
    }

    [Fact]
    public void StoredPage_IsServedBackByIdentity()
    {
        var cache = new RenderedPageCache(1024);
        var src = WriteSource("a.pdf", [1, 2, 3]);
        byte[] png = [9, 9];

        cache.Store(src.Path, 150, 1, png, src.Length, src.LastWriteUtc);

        Assert.Same(png, cache.TryGet(src.Path, 150, 1));
    }

    [Fact]
    public void KeysAreIndependent_PerDpiAndPage()
    {
        var cache = new RenderedPageCache(1024);
        var src = WriteSource("a.pdf", [1, 2, 3]);

        cache.Store(src.Path, 150, 1, [1], src.Length, src.LastWriteUtc);

        Assert.NotNull(cache.TryGet(src.Path, 150, 1));
        Assert.Null(cache.TryGet(src.Path, 150, 2));
        Assert.Null(cache.TryGet(src.Path, 300, 1));
    }

    [Fact]
    public void ChangedSourceFile_InvalidatesItsRenders()
    {
        var cache = new RenderedPageCache(1024);
        var src = WriteSource("a.pdf", [1, 2, 3]);
        cache.Store(src.Path, 150, 1, [9], src.Length, src.LastWriteUtc);

        File.WriteAllBytes(src.Path, [7, 7, 7, 7]); // different length → stamp mismatch regardless of timer resolution

        Assert.Null(cache.TryGet(src.Path, 150, 1));
    }

    [Fact]
    public void LeastRecentlyUsedPage_IsEvictedWhenOverBudget()
    {
        var cache = new RenderedPageCache(8);
        var src = WriteSource("a.pdf", [1]);

        cache.Store(src.Path, 150, 1, new byte[4], src.Length, src.LastWriteUtc);
        cache.Store(src.Path, 150, 2, new byte[4], src.Length, src.LastWriteUtc);
        Assert.NotNull(cache.TryGet(src.Path, 150, 1)); // touch page 1 → page 2 becomes LRU

        cache.Store(src.Path, 150, 3, new byte[4], src.Length, src.LastWriteUtc); // evicts page 2

        Assert.NotNull(cache.TryGet(src.Path, 150, 1));
        Assert.Null(cache.TryGet(src.Path, 150, 2));
        Assert.NotNull(cache.TryGet(src.Path, 150, 3));
    }

    [Fact]
    public void DisabledCache_NeverServes()
    {
        var cache = new RenderedPageCache(0);
        var src = WriteSource("a.pdf", [1]);

        cache.Store(src.Path, 150, 1, [9], src.Length, src.LastWriteUtc);

        Assert.Null(cache.TryGet(src.Path, 150, 1));
    }

    [Fact]
    public void MissingSourceFile_NeverServes()
    {
        var cache = new RenderedPageCache(1024);
        var src = WriteSource("a.pdf", [1]);
        cache.Store(src.Path, 150, 1, [9], src.Length, src.LastWriteUtc);

        File.Delete(src.Path);

        Assert.Null(cache.TryGet(src.Path, 150, 1));
    }
}
