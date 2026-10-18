// Animated PNG (APNG) reader and writer, written from the APNG specification (PNG 3rd edition, W3C, and Mozilla's
// original APNG 1.0 description). ImageMagick reads only the default image of an APNG and writes APNG through an
// external FFmpeg, so Filee assembles the chunks itself: every frame is an ordinary PNG encoded or decoded by
// ImageMagick, the animation is acTL (frame count, loops) plus one fcTL (region, delay, dispose and blend operations)
// per frame, the first frame's data in IDAT and the others' in fdAT chunks with a shared sequence number.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Archives;
using ImageMagick;
using ImageMagick.Formats;

namespace Filee.Engines.Magick;

/// <summary>Reads APNG files into fully composed frames and writes frames as APNG.</summary>
internal static class Apng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The ticks per second of frame delays handed to ImageMagick (GIF's hundredths of a second).</summary>
    private const int TicksPerSecond = 100;

    /// <summary>Largest IDAT / fdAT chunk written; any size up to 2³¹ − 1 is allowed, smaller ones stream better.</summary>
    private const int MaxChunkData = 1 << 20;

    private const byte DisposeNone = 0, DisposeBackground = 1, DisposePrevious = 2;
    private const byte BlendSource = 0, BlendOver = 1;

    private sealed record Chunk(string Type, byte[] Data);

    /// <summary>One fcTL with the image data that follows it.</summary>
    private sealed class Frame(uint width, uint height, uint x, uint y, ushort delayNumerator, ushort delayDenominator, byte dispose, byte blend)
    {
        public uint Width { get; } = width;
        public uint Height { get; } = height;
        public uint X { get; } = x;
        public uint Y { get; } = y;
        public ushort DelayNumerator { get; } = delayNumerator;
        public ushort DelayDenominator { get; } = delayDenominator;
        public byte Dispose { get; } = dispose;
        public byte Blend { get; } = blend;
        public MemoryStream Data { get; } = new();
    }

    /// <summary>
    /// True when <paramref name="path"/> is a PNG with an acTL chunk before its image data, i.e. an animated PNG
    /// (often saved with the .png extension).
    /// </summary>
    public static bool IsAnimated(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8 || !header.SequenceEqual(Signature))
                return false;
            while (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) == 8)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header[4..]);
                if (type == "acTL")
                    return true;
                if (type is "IDAT" or "IEND" || length > int.MaxValue)
                    return false;
                stream.Seek(length + 4L, SeekOrigin.Current); // data and CRC
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every frame of an APNG, each composed onto the canvas as a player shows it (dispose and blend operations
    /// applied), with its delay and the loop count. A PNG without animation comes back as its one image; the default
    /// image of an APNG whose first frame comes after it is not part of the animation and is skipped.
    /// </summary>
    public static MagickImageCollection Read(string path)
    {
        var chunks = ReadChunks(File.ReadAllBytes(path));
        if (chunks.Count == 0 || chunks[0].Type != "IHDR" || chunks[0].Data.Length != 13)
            throw new InvalidDataException("The file is not a PNG image or is damaged.");
        var header = chunks[0].Data;
        var canvasWidth = BinaryPrimitives.ReadUInt32BigEndian(header);
        var canvasHeight = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));

        var animation = chunks.FirstOrDefault(c => c.Type == "acTL");
        if (animation is null || animation.Data.Length != 8)
            return new MagickImageCollection(path, new MagickReadSettings { Format = MagickFormat.Png });
        var plays = BinaryPrimitives.ReadUInt32BigEndian(animation.Data.AsSpan(4));

        // Chunks every frame needs to decode like the default image: palette, transparency, colour space.
        var shared = new List<Chunk>();
        var frames = new List<Frame>();
        Frame? current = null;
        var seenImageData = false;
        foreach (var chunk in chunks.Skip(1))
        {
            switch (chunk.Type)
            {
                case "fcTL":
                    current = ParseFrameControl(chunk.Data, canvasWidth, canvasHeight);
                    frames.Add(current);
                    break;
                case "IDAT":
                    seenImageData = true;
                    current?.Data.Write(chunk.Data); // without an fcTL before it, the default image is not a frame
                    break;
                case "fdAT":
                    if (chunk.Data.Length < 4 || current is null)
                        throw new InvalidDataException("The APNG file is damaged (fdAT chunk).");
                    current.Data.Write(chunk.Data.AsSpan(4)); // after the 4-byte sequence number
                    break;
                case "IEND":
                case "acTL":
                    break;
                default:
                    if (!seenImageData)
                        shared.Add(chunk);
                    break;
            }
        }
        frames.RemoveAll(f => f.Data.Length == 0);
        if (frames.Count == 0)
            throw new InvalidDataException("The APNG file has no frames.");

        var collection = new MagickImageCollection();
        try
        {
            using var canvas = new MagickImage(MagickColors.Transparent, canvasWidth, canvasHeight);
            canvas.ColorSpace = ColorSpace.sRGB;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                using var picture = new MagickImage(FramePng(header, shared, frame), new MagickReadSettings { Format = MagickFormat.Png });
                picture.Alpha(AlphaOption.Set);

                var dispose = i == 0 && frame.Dispose == DisposePrevious ? DisposeBackground : frame.Dispose;
                using var previous = dispose == DisposePrevious ? canvas.Clone() : null;

                canvas.Composite(picture, (int)frame.X, (int)frame.Y,
                    frame.Blend == BlendOver ? CompositeOperator.Over : CompositeOperator.Copy, Channels.All);

                var composed = canvas.Clone();
                composed.Page = new MagickGeometry(0, 0, canvasWidth, canvasHeight);
                composed.AnimationTicksPerSecond = TicksPerSecond;
                composed.AnimationDelay = DelayTicks(frame.DelayNumerator, frame.DelayDenominator);
                composed.AnimationIterations = plays;
                // Every frame is the whole picture: the next one replaces it instead of being drawn over it.
                composed.GifDisposeMethod = GifDisposeMethod.Background;
                collection.Add(composed);

                if (dispose == DisposeBackground)
                {
                    using var clear = new MagickImage(MagickColors.Transparent, frame.Width, frame.Height);
                    canvas.Composite(clear, (int)frame.X, (int)frame.Y, CompositeOperator.Copy, Channels.All);
                }
                else if (previous is not null)
                {
                    canvas.Composite(previous, 0, 0, CompositeOperator.Copy, Channels.All);
                }
            }
            return collection;
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    /// <summary>Delay in hundredths of a second; a denominator of 0 means hundredths (APNG specification).</summary>
    private static uint DelayTicks(ushort numerator, ushort denominator) =>
        (uint)Math.Round(numerator * (double)TicksPerSecond / (denominator == 0 ? 100 : denominator));

    private static Frame ParseFrameControl(byte[] data, uint canvasWidth, uint canvasHeight)
    {
        if (data.Length != 26)
            throw new InvalidDataException("The APNG file is damaged (fcTL chunk).");
        var span = data.AsSpan();
        var frame = new Frame(
            width: BinaryPrimitives.ReadUInt32BigEndian(span[4..]),
            height: BinaryPrimitives.ReadUInt32BigEndian(span[8..]),
            x: BinaryPrimitives.ReadUInt32BigEndian(span[12..]),
            y: BinaryPrimitives.ReadUInt32BigEndian(span[16..]),
            delayNumerator: BinaryPrimitives.ReadUInt16BigEndian(span[20..]),
            delayDenominator: BinaryPrimitives.ReadUInt16BigEndian(span[22..]),
            dispose: span[24],
            blend: span[25]);
        if (frame.Width == 0 || frame.Height == 0 || (ulong)frame.X + frame.Width > canvasWidth
            || (ulong)frame.Y + frame.Height > canvasHeight || frame.Dispose > DisposePrevious || frame.Blend > BlendOver)
            throw new InvalidDataException("The APNG file is damaged (a frame lies outside the picture).");
        return frame;
    }

    /// <summary>A standalone PNG of one frame: the file's header with the frame's size, the shared chunks and its data.</summary>
    private static byte[] FramePng(byte[] header, List<Chunk> shared, Frame frame)
    {
        using var png = new MemoryStream();
        png.Write(Signature);
        var frameHeader = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader, frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(4), frame.Height);
        WriteChunk(png, "IHDR", frameHeader);
        foreach (var chunk in shared)
            WriteChunk(png, chunk.Type, chunk.Data);
        WriteChunk(png, "IDAT", frame.Data.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="frames"/> as one APNG. Frames are placed at the top left of a canvas as large as the
    /// largest frame. Each frame after the first stores only the rectangle that changed (dispose "none", blend
    /// "source"), and a frame identical to the one before only lengthens its delay. Every frame is 8-bit RGB, or RGBA
    /// when any frame has transparency, because all frames of an APNG share the header's pixel format.
    /// </summary>
    public static void Write(IReadOnlyList<IMagickImage<byte>> frames, string path)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames to write.", nameof(frames));
        var width = frames.Max(f => f.Width);
        var height = frames.Max(f => f.Height);
        // Smaller frames are padded with transparency.
        var alpha = frames.Any(f => f.HasAlpha || f.Width != width || f.Height != height);
        var format = alpha ? MagickFormat.Png32 : MagickFormat.Png24;

        // Full-canvas pixels of every frame, to find what changed between them.
        var pictures = new List<(MagickImage Image, byte[] Pixels, uint DelayNumerator, uint DelayDenominator)>();
        try
        {
            foreach (var frame in frames)
            {
                var image = (MagickImage)frame.Clone();
                image.Page = new MagickGeometry(0, 0, image.Width, image.Height);
                if (image.Width != width || image.Height != height)
                {
                    image.Alpha(AlphaOption.Set);
                    image.BackgroundColor = MagickColors.Transparent;
                    image.Extent(width, height, Gravity.Northwest);
                }
                var pixels = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
                var ticks = frame.AnimationTicksPerSecond > 0 ? (uint)frame.AnimationTicksPerSecond : TicksPerSecond;
                if (pictures.Count > 0 && pixels.AsSpan().SequenceEqual(pictures[^1].Pixels) && pictures[^1].DelayDenominator == ticks)
                {
                    var last = pictures[^1];
                    pictures[^1] = last with { DelayNumerator = last.DelayNumerator + frame.AnimationDelay };
                    image.Dispose();
                    continue;
                }
                pictures.Add((image, pixels, frame.AnimationDelay, ticks));
            }

            using var output = new MemoryStream();
            output.Write(Signature);
            byte[]? header = null;
            var sequence = 0u;
            for (var i = 0; i < pictures.Count; i++)
            {
                var (image, pixels, delayNumerator, delayDenominator) = pictures[i];
                var region = i == 0 ? (X: 0u, Y: 0u, Width: width, Height: height) : ChangedRegion(pictures[i - 1].Pixels, pixels, width, height);
                using var part = region.Width == width && region.Height == height
                    ? (MagickImage)image.Clone()
                    : (MagickImage)image.CloneArea(new MagickGeometry((int)region.X, (int)region.Y, region.Width, region.Height));
                part.Page = new MagickGeometry(0, 0, part.Width, part.Height);
                part.Settings.SetDefines(new PngWriteDefines { ExcludeChunks = PngChunkFlags.date });
                var encoded = ReadChunks(part.ToByteArray(format));
                var data = encoded.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();

                if (i == 0)
                {
                    header = encoded[0].Data;
                    WriteChunk(output, "IHDR", header);
                    // Colour information and metadata of the first frame apply to the whole animation.
                    foreach (var chunk in encoded.Skip(1).TakeWhile(c => c.Type != "IDAT"))
                        WriteChunk(output, chunk.Type, chunk.Data);
                    var control = new byte[8];
                    BinaryPrimitives.WriteUInt32BigEndian(control, (uint)pictures.Count);
                    BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), frames[0].AnimationIterations);
                    WriteChunk(output, "acTL", control);
                }
                else if (!encoded[0].Data.AsSpan(8).SequenceEqual(header.AsSpan(8)))
                {
                    throw new InvalidOperationException("APNG frames were encoded with different pixel formats.");
                }

                var (numerator, denominator) = FitDelay(delayNumerator, delayDenominator);
                var frameControl = new byte[26];
                var span = frameControl.AsSpan();
                BinaryPrimitives.WriteUInt32BigEndian(span, sequence++);
                BinaryPrimitives.WriteUInt32BigEndian(span[4..], part.Width);
                BinaryPrimitives.WriteUInt32BigEndian(span[8..], part.Height);
                BinaryPrimitives.WriteUInt32BigEndian(span[12..], region.X);
                BinaryPrimitives.WriteUInt32BigEndian(span[16..], region.Y);
                BinaryPrimitives.WriteUInt16BigEndian(span[20..], numerator);
                BinaryPrimitives.WriteUInt16BigEndian(span[22..], denominator);
                span[24] = DisposeNone;
                span[25] = BlendSource;
                WriteChunk(output, "fcTL", frameControl);

                var offset = 0;
                do
                {
                    var piece = data.AsSpan(offset, Math.Min(MaxChunkData, data.Length - offset));
                    offset += piece.Length;
                    if (i == 0)
                    {
                        WriteChunk(output, "IDAT", piece);
                        continue;
                    }
                    var sequenced = new byte[piece.Length + 4];
                    BinaryPrimitives.WriteUInt32BigEndian(sequenced, sequence++);
                    piece.CopyTo(sequenced.AsSpan(4));
                    WriteChunk(output, "fdAT", sequenced);
                }
                while (offset < data.Length);
            }
            WriteChunk(output, "IEND", []);
            File.WriteAllBytes(path, output.ToArray());
        }
        finally
        {
            foreach (var picture in pictures)
                picture.Image.Dispose();
        }
    }

    /// <summary>The smallest rectangle holding every pixel that differs (at least one pixel).</summary>
    private static (uint X, uint Y, uint Width, uint Height) ChangedRegion(byte[] before, byte[] after, uint width, uint height)
    {
        var stride = (int)width * 4;
        int top = -1, bottom = -1, left = (int)width, right = -1;
        for (var y = 0; y < height; y++)
        {
            var rowBefore = before.AsSpan(y * stride, stride);
            var rowAfter = after.AsSpan(y * stride, stride);
            if (rowBefore.SequenceEqual(rowAfter))
                continue;
            if (top < 0)
                top = y;
            bottom = y;
            var first = rowBefore.CommonPrefixLength(rowAfter) / 4;
            var last = (int)width - 1;
            while (last > first && rowBefore.Slice(last * 4, 4).SequenceEqual(rowAfter.Slice(last * 4, 4)))
                last--;
            left = Math.Min(left, first);
            right = Math.Max(right, last);
        }
        if (top < 0)
            return (0, 0, 1, 1); // only the delay differs (another tick rate): repeat one pixel
        return ((uint)left, (uint)top, (uint)(right - left + 1), (uint)(bottom - top + 1));
    }

    /// <summary>Scales a delay down until both parts fit the 16-bit fields of fcTL.</summary>
    private static (ushort Numerator, ushort Denominator) FitDelay(uint numerator, uint denominator)
    {
        while (numerator > ushort.MaxValue || denominator > ushort.MaxValue)
        {
            numerator = (numerator + 1) / 2;
            denominator = Math.Max(1, (denominator + 1) / 2);
        }
        return ((ushort)numerator, (ushort)denominator);
    }

    private static List<Chunk> ReadChunks(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("The file is not a PNG image.");
        var chunks = new List<Chunk>();
        var position = 8;
        while (position + 12 <= file.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            if (length > (uint)(file.Length - position - 12))
                throw new InvalidDataException("The PNG file is damaged (a chunk runs past the end).");
            var type = Encoding.ASCII.GetString(file, position + 4, 4);
            chunks.Add(new Chunk(type, file.AsSpan(position + 8, (int)length).ToArray()));
            position += 12 + (int)length;
            if (type == "IEND")
                break;
        }
        return chunks;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        stream.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc.Value);
        stream.Write(number);
    }
}
