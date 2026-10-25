// A read-only wrapper that counts the bytes read through it and fails once they pass a limit, so a ZIP entry that
// unpacks to far more than its header says (a ZIP bomb) stops at a known size instead of filling the memory.

namespace Filee.Engines.Geo;

/// <summary>Reads at most <c>limit</c> bytes of <paramref name="inner"/>, then throws <see cref="InvalidDataException"/>.</summary>
internal sealed class LimitedStream(Stream inner, long limit, string message) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        _read += read;
        if (_read > limit)
            throw new InvalidDataException(message);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
