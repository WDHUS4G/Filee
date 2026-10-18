// A read-only window of a stream: the compressed data of one archive entry, so a decoder can never read into the
// next entry's headers.

namespace Filee.Engines.Archives;

/// <summary>Reads at most <c>length</c> bytes of an inner stream from its current position.</summary>
internal sealed class BoundedStream(Stream inner, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
            return 0;
        if (buffer.Length > _remaining)
            buffer = buffer[..(int)_remaining];
        var read = inner.Read(buffer);
        if (read == 0)
            throw new EndOfStreamException("The archive ends in the middle of an entry.");
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
