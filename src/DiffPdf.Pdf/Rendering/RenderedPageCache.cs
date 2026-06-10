namespace DiffPdf.Pdf.Rendering;

/// <summary>
/// Bounded, LRU-evicted cache of rendered page PNGs keyed by (path, dpi, page), validated against the source
/// file's length + last-write stamp captured before the render. Holds the byproducts of a Ghostscript block
/// render until the engine consumes them — a short-lived prefetch buffer, not a long-lived cache, so the
/// budget can stay small. Thread-safe; capacity ≤ 0 disables it (every <see cref="TryGet"/> misses).
/// </summary>
internal sealed class RenderedPageCache(long capacityBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Path, int Dpi, int Page), Entry> _entries = new();
    private long _used;
    private long _clock; // monotonic LRU tick

    private sealed class Entry(byte[] png, long fileLength, DateTime fileLastWriteUtc)
    {
        public byte[] Png { get; } = png;
        public long FileLength { get; } = fileLength;
        public DateTime FileLastWriteUtc { get; } = fileLastWriteUtc;
        public long LastUsed { get; set; }
    }

    public byte[]? TryGet(string path, int dpi, int page)
    {
        if (capacityBytes <= 0) return null;

        var info = new FileInfo(path);
        if (!info.Exists) return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue((path, dpi, page), out var entry)) return null;

            // A replaced/edited source invalidates its renders — the comparison must never see stale pixels.
            if (entry.FileLength != info.Length || entry.FileLastWriteUtc != info.LastWriteTimeUtc)
            {
                _entries.Remove((path, dpi, page));
                _used -= entry.Png.LongLength;
                return null;
            }

            entry.LastUsed = ++_clock;
            return entry.Png;
        }
    }

    public void Store(string path, int dpi, int page, byte[] png, long fileLength, DateTime fileLastWriteUtc)
    {
        if (capacityBytes <= 0 || png.LongLength > capacityBytes) return;

        lock (_gate)
        {
            if (_entries.Remove((path, dpi, page), out var replaced))
                _used -= replaced.Png.LongLength;

            while (_used + png.LongLength > capacityBytes && _entries.Count > 0)
            {
                (string, int, int) lruKey = default!;
                long oldest = long.MaxValue;
                foreach (var (key, entry) in _entries)
                {
                    if (entry.LastUsed < oldest) { oldest = entry.LastUsed; lruKey = key; }
                }
                _used -= _entries[lruKey].Png.LongLength;
                _entries.Remove(lruKey);
            }

            _entries[(path, dpi, page)] = new Entry(png, fileLength, fileLastWriteUtc) { LastUsed = ++_clock };
            _used += png.LongLength;
        }
    }
}
