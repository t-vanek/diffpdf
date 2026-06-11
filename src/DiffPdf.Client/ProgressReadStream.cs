namespace DiffPdf.Client;

/// <summary>
/// Read-only passthrough that reports cumulative read progress as a 0..1 fraction. Wraps the source
/// stream of a multipart upload so the desktop can drive a per-file progress bar from the bytes
/// HttpClient has actually pulled off the stream. Owns (and disposes) the inner stream, matching
/// StreamContent's ownership semantics. With an unknown total length no fractions are reported.
/// </summary>
internal sealed class ProgressReadStream(Stream inner, IProgress<double> progress, long? totalBytes) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Report(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Report(int read)
    {
        _read += read;
        if (totalBytes is > 0)
            progress.Report(Math.Min(1d, (double)_read / totalBytes.Value));
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
