using DiffPdf.Pdf.Rendering;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Contract of <see cref="PdfBytesCache"/>: identity caching, freshness validation against the file
/// stamp, LRU eviction under the byte budget, read-through for oversized files, and the disabled mode.
/// </summary>
public sealed class PdfBytesCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diffpdf-bytescache-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task SecondRead_ReturnsTheCachedArrayInstance()
    {
        var cache = new PdfBytesCache(1024);
        string path = WriteFile("a.pdf", [1, 2, 3]);

        var first = await cache.GetAsync(path);
        var second = await cache.GetAsync(path);

        Assert.Same(first, second);
        Assert.Equal([1, 2, 3], first);
    }

    [Fact]
    public void SyncGet_SharesTheSameCache()
    {
        var cache = new PdfBytesCache(1024);
        string path = WriteFile("a.pdf", [1, 2, 3]);

        Assert.Same(cache.Get(path), cache.Get(path));
    }

    [Fact]
    public async Task ChangedFile_IsReloaded()
    {
        var cache = new PdfBytesCache(1024);
        string path = WriteFile("a.pdf", [1, 2, 3]);
        var stale = await cache.GetAsync(path);

        File.WriteAllBytes(path, [9, 9, 9, 9]); // different length → stamp mismatch regardless of timer resolution

        var fresh = await cache.GetAsync(path);
        Assert.NotSame(stale, fresh);
        Assert.Equal([9, 9, 9, 9], fresh);
    }

    [Fact]
    public async Task LeastRecentlyUsedEntry_IsEvictedWhenOverBudget()
    {
        var cache = new PdfBytesCache(8);
        string a = WriteFile("a.pdf", new byte[4]);
        string b = WriteFile("b.pdf", new byte[4]);
        string c = WriteFile("c.pdf", new byte[4]);

        var aBytes = await cache.GetAsync(a);
        await cache.GetAsync(b);
        await cache.GetAsync(a); // touch a → b becomes the LRU entry
        await cache.GetAsync(c); // over budget → evicts b, keeps a

        Assert.Same(aBytes, await cache.GetAsync(a));
    }

    [Fact]
    public async Task FileLargerThanBudget_IsReadThroughNotCached()
    {
        var cache = new PdfBytesCache(4);
        string path = WriteFile("big.pdf", new byte[16]);

        var first = await cache.GetAsync(path);
        var second = await cache.GetAsync(path);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DisabledCache_AlwaysReadsFresh()
    {
        var cache = new PdfBytesCache(0);
        string path = WriteFile("a.pdf", [1, 2, 3]);

        Assert.NotSame(await cache.GetAsync(path), await cache.GetAsync(path));
    }

    [Fact]
    public async Task MissingFile_ThrowsFileNotFound()
    {
        var cache = new PdfBytesCache(1024);
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await cache.GetAsync(Path.Combine(_dir, "missing.pdf")));
    }
}
