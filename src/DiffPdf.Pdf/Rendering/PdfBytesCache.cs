namespace DiffPdf.Pdf.Rendering;

/// <summary>
/// Bounded, LRU-evicted cache of whole-file PDF bytes keyed by path, validated against file length and
/// last-write time on every hit (a metadata check — far cheaper than re-reading megabytes over a share).
/// Built for <see cref="PdfiumPageRenderer"/>, whose native API needs the full document per page render.
/// Thread-safe; files larger than the capacity are read through without being cached; capacity ≤ 0 disables
/// caching entirely (every call reads the file).
/// </summary>
public sealed class PdfBytesCache(long capacityBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private long _used;
    private long _clock; // monotonic LRU tick

    private sealed class Entry(byte[] bytes, DateTime lastWriteUtc)
    {
        public byte[] Bytes { get; } = bytes;
        public DateTime LastWriteUtc { get; } = lastWriteUtc;
        public long LastUsed { get; set; }
    }

    public byte[] Get(string path)
    {
        var (hit, stamp) = TryGet(path);
        if (hit is not null) return hit;

        byte[] bytes = File.ReadAllBytes(path);
        Store(path, bytes, stamp);
        return bytes;
    }

    public async ValueTask<byte[]> GetAsync(string path, CancellationToken ct = default)
    {
        var (hit, stamp) = TryGet(path);
        if (hit is not null) return hit;

        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        Store(path, bytes, stamp);
        return bytes;
    }

    /// <summary>One metadata read decides both freshness and (on miss) the stamp the fresh entry is stored under.
    /// A file that changes between the stamp and the read self-heals: the next hit sees a stamp mismatch and reloads.</summary>
    private (byte[]? Bytes, DateTime LastWriteUtc) TryGet(string path)
    {
        if (capacityBytes <= 0) return (null, default);

        var info = new FileInfo(path);
        if (!info.Exists) return (null, default); // let the read surface the real FileNotFoundException

        long length = info.Length;
        DateTime lastWrite = info.LastWriteTimeUtc;

        lock (_gate)
        {
            if (_byPath.TryGetValue(path, out var entry)
                && entry.Bytes.LongLength == length
                && entry.LastWriteUtc == lastWrite)
            {
                entry.LastUsed = ++_clock;
                return (entry.Bytes, lastWrite);
            }
        }

        return (null, lastWrite);
    }

    private void Store(string path, byte[] bytes, DateTime lastWriteUtc)
    {
        if (capacityBytes <= 0 || bytes.LongLength > capacityBytes) return;

        lock (_gate)
        {
            if (_byPath.Remove(path, out var replaced))
                _used -= replaced.Bytes.LongLength;

            while (_used + bytes.LongLength > capacityBytes && _byPath.Count > 0)
            {
                string lruKey = null!;
                long oldest = long.MaxValue;
                foreach (var (key, entry) in _byPath)
                {
                    if (entry.LastUsed < oldest) { oldest = entry.LastUsed; lruKey = key; }
                }
                _used -= _byPath[lruKey].Bytes.LongLength;
                _byPath.Remove(lruKey);
            }

            _byPath[path] = new Entry(bytes, lastWriteUtc) { LastUsed = ++_clock };
            _used += bytes.LongLength;
        }
    }
}
