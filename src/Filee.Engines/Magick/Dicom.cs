// DICOM images (medical scans) stored without compression, read by Filee itself, written from the DICOM standard
// (PS3.5 data encoding, PS3.3 C.7.6.3 image pixel module, C.11.2 VOI LUT). ImageMagick's Q8 build reads only 8-bit
// DICOM: a 12- or 16-bit grey image, which CT, MR and X-ray scanners write, needs a palette larger than it has.
// Here every frame becomes an 8-bit picture: stored values → rescale slope / intercept → the file's first window
// (centre / width), or the full range of values when there is none. Compressed pixel data (JPEG, JPEG 2000, RLE, …)
// is left to ImageMagick.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Reads uncompressed DICOM images into 8-bit frames.</summary>
internal static class Dicom
{
    private const uint UndefinedLength = 0xFFFFFFFF;

    private const string ImplicitLittleEndian = "1.2.840.10008.1.2";
    private const string ExplicitLittleEndian = "1.2.840.10008.1.2.1";
    private const string DeflatedExplicitLittleEndian = "1.2.840.10008.1.2.1.99";
    private const string ExplicitBigEndian = "1.2.840.10008.1.2.2";

    /// <summary>Value representations with a reserved 16 bits and a 32-bit length in explicit VR encoding.</summary>
    private static readonly HashSet<string> LongVrs = ["OB", "OD", "OF", "OL", "OV", "OW", "SQ", "SV", "UC", "UN", "UR", "UT", "UV"];

    private const uint PixelDataTag = 0x7FE00010;

    /// <summary>Elements of the image pixel module (and rescale / window) this reader uses.</summary>
    private static readonly HashSet<uint> Wanted =
    [
        0x00280002, 0x00280004, 0x00280006, 0x00280008, 0x00280010, 0x00280011, 0x00280100, 0x00280101, 0x00280102,
        0x00280103, 0x00281050, 0x00281051, 0x00281052, 0x00281053, PixelDataTag,
    ];

    /// <summary>
    /// The frames of <paramref name="path"/>, or null when its pixel data is compressed or laid out in a way this reader
    /// does not handle (palette colour, YBR subsampled), for ImageMagick to try.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not DICOM or is damaged.</exception>
    public static MagickImageCollection? TryRead(string path)
    {
        var file = File.ReadAllBytes(path);
        var position = file.Length >= 132 && file.AsSpan(128, 4).SequenceEqual("DICM"u8) ? 132 : 0;

        // File meta information (group 0002) is always explicit VR little endian.
        string? syntax = null;
        var meta = new Reader(file, position, explicitVr: true, bigEndian: false);
        while (meta.Position + 4 <= file.Length && meta.PeekGroup() == 0x0002)
        {
            var element = meta.Next();
            if (element.Tag == 0x00020010)
                syntax = Text(element.Value);
        }

        var data = file;
        var start = meta.Position;
        bool explicitVr, bigEndian = false;
        switch (syntax)
        {
            case null:
                // No meta information (older files): explicit if a value representation follows the first tag.
                explicitVr = start + 6 <= file.Length && char.IsAsciiLetterUpper((char)file[start + 4]) && char.IsAsciiLetterUpper((char)file[start + 5]);
                break;
            case ImplicitLittleEndian:
                explicitVr = false;
                break;
            case ExplicitLittleEndian:
                explicitVr = true;
                break;
            case ExplicitBigEndian:
                explicitVr = true;
                bigEndian = true;
                break;
            case DeflatedExplicitLittleEndian:
                using (var inflated = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(new MemoryStream(file, start, file.Length - start), CompressionMode.Decompress))
                        deflate.CopyTo(inflated);
                    data = inflated.ToArray();
                }
                start = 0;
                explicitVr = true;
                break;
            default:
                return null; // compressed (encapsulated) pixel data
        }

        var values = new Dictionary<uint, byte[]>();
        var reader = new Reader(data, start, explicitVr, bigEndian);
        while (reader.Position + 8 <= data.Length)
        {
            var element = reader.Next();
            if (element.Undefined)
            {
                if (element.Tag == PixelDataTag)
                    return null; // encapsulated: compressed frames
                reader.SkipSequence();
                continue;
            }
            if (Wanted.Contains(element.Tag))
                values[element.Tag] = element.Value;
            if (element.Tag == PixelDataTag)
                break;
        }
        if (!values.TryGetValue(PixelDataTag, out var pixels))
            throw new InvalidDataException("The DICOM file has no image.");
        return Decode(values, pixels, bigEndian);
    }

    private static MagickImageCollection? Decode(Dictionary<uint, byte[]> values, byte[] pixels, bool bigEndian)
    {
        int Number(uint tag, int fallback) => values.TryGetValue(tag, out var v) && v.Length >= 2
            ? bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(v) : BinaryPrimitives.ReadUInt16LittleEndian(v) : fallback;
        double? Decimal(uint tag) => values.TryGetValue(tag, out var v) && double.TryParse(Text(v).Split('\\')[0],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        var samples = Number(0x00280002, 1);
        var photometric = values.TryGetValue(0x00280004, out var p) ? Text(p) : "MONOCHROME2";
        var planar = Number(0x00280006, 0);
        var frames = (int)(Decimal(0x00280008) ?? 1);
        var rows = Number(0x00280010, 0);
        var columns = Number(0x00280011, 0);
        var allocated = Number(0x00280100, 8);
        var stored = Number(0x00280101, allocated);
        var highBit = Number(0x00280102, stored - 1);
        var signed = Number(0x00280103, 0) == 1;
        var slope = Decimal(0x00281053) ?? 1;
        var intercept = Decimal(0x00281052) ?? 0;
        var center = Decimal(0x00281050);
        var width = Decimal(0x00281051);

        var grey = samples == 1 && photometric is "MONOCHROME1" or "MONOCHROME2";
        var colour = samples == 3 && photometric is "RGB" or "YBR_FULL";
        if (!grey && !colour || allocated is not (8 or 16) || stored is < 1 or > 16 || stored > allocated || highBit >= allocated)
            return null;
        if (rows == 0 || columns == 0 || frames < 1)
            throw new InvalidDataException("The DICOM image has no size.");
        var pixelCount = rows * columns;
        var frameBytes = (long)pixelCount * samples * allocated / 8;
        if (pixels.Length < frameBytes * frames)
            throw new InvalidDataException("The DICOM file is damaged (its image data is incomplete).");

        var shift = highBit - stored + 1;
        var mask = (1 << stored) - 1;
        int Raw(int frame, int index)
        {
            var offset = frame * frameBytes + (long)index * allocated / 8;
            int value = allocated == 8 ? pixels[offset]
                : bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(pixels.AsSpan((int)offset))
                : BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan((int)offset));
            value = (value >> shift) & mask;
            return signed && (value & (1 << (stored - 1))) != 0 ? value - (1 << stored) : value;
        }

        // Grey: values → 0..255 through the window, or the range of values in the whole file without one.
        Func<double, double> toGrey;
        if (grey && width is >= 1 && center is { } c)
        {
            var w = width.Value;
            toGrey = x => x <= c - 0.5 - (w - 1) / 2 ? 0 : x > c - 0.5 + (w - 1) / 2 ? 255 : ((x - (c - 0.5)) / (w - 1) + 0.5) * 255;
        }
        else if (grey)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (var f = 0; f < frames; f++)
                for (var i = 0; i < pixelCount; i++)
                {
                    var x = Raw(f, i) * slope + intercept;
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                }
            var range = max - min;
            toGrey = x => range <= 0 ? 0 : (x - min) / range * 255;
        }
        else
        {
            toGrey = x => x;
        }

        var collection = new MagickImageCollection();
        try
        {
            for (var f = 0; f < frames; f++)
            {
                var rgb = new byte[pixelCount * 3];
                for (var i = 0; i < pixelCount; i++)
                {
                    if (grey)
                    {
                        var level = Math.Clamp(toGrey(Raw(f, i) * slope + intercept), 0, 255);
                        var value = (byte)Math.Round(photometric == "MONOCHROME1" ? 255 - level : level);
                        rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = value;
                        continue;
                    }
                    var (a, b, d) = planar == 1
                        ? (Raw(f, i), Raw(f, pixelCount + i), Raw(f, 2 * pixelCount + i))
                        : (Raw(f, i * 3), Raw(f, i * 3 + 1), Raw(f, i * 3 + 2));
                    var down = Math.Max(0, stored - 8);
                    (a, b, d) = (a >> down, b >> down, d >> down);
                    if (photometric == "YBR_FULL")
                        (a, b, d) = ((int)Math.Round(a + 1.402 * (d - 128)), (int)Math.Round(a - 0.344136 * (b - 128) - 0.714136 * (d - 128)),
                            (int)Math.Round(a + 1.772 * (b - 128)));
                    rgb[i * 3] = (byte)Math.Clamp(a, 0, 255);
                    rgb[i * 3 + 1] = (byte)Math.Clamp(b, 0, 255);
                    rgb[i * 3 + 2] = (byte)Math.Clamp(d, 0, 255);
                }
                var image = new MagickImage();
                image.ReadPixels(rgb, new PixelReadSettings((uint)columns, (uint)rows, StorageType.Char, PixelMapping.RGB));
                if (grey)
                    image.ColorType = ColorType.Grayscale;
                collection.Add(image);
            }
            return collection;
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    private static string Text(byte[] value) => Encoding.ASCII.GetString(value).TrimEnd('\0', ' ');

    private readonly record struct Element(uint Tag, byte[] Value, bool Undefined);

    /// <summary>Reads data elements one after the other (PS3.5 chapter 7).</summary>
    private sealed class Reader(byte[] data, int position, bool explicitVr, bool bigEndian)
    {
        public int Position { get; private set; } = position;

        public ushort PeekGroup() => U16(Position);

        public Element Next()
        {
            if (Position + 8 > data.Length)
                throw Damaged();
            var tag = (uint)U16(Position) << 16 | U16(Position + 2);
            uint length;
            if (explicitVr && tag >> 16 != 0xFFFE)
            {
                var vr = Encoding.ASCII.GetString(data, Position + 4, 2);
                if (LongVrs.Contains(vr))
                {
                    if (Position + 12 > data.Length)
                        throw Damaged();
                    length = U32(Position + 8);
                    Position += 12;
                }
                else
                {
                    length = U16(Position + 6);
                    Position += 8;
                }
            }
            else
            {
                length = U32(Position + 4);
                Position += 8;
            }
            if (length == UndefinedLength)
                return new Element(tag, [], true);
            if (length > data.Length - Position)
                throw Damaged();
            var value = data.AsSpan(Position, (int)length).ToArray();
            Position += (int)length;
            return new Element(tag, value, false);
        }

        /// <summary>Skips a sequence of undefined length: items until the sequence delimitation item.</summary>
        public void SkipSequence()
        {
            while (true)
            {
                var item = Header();
                if (item.Tag == 0xFFFEE0DD)
                    return;
                if (item.Tag != 0xFFFEE000)
                    throw Damaged();
                if (item.Length != UndefinedLength)
                {
                    Skip(item.Length);
                    continue;
                }
                // An item of undefined length: elements until the item delimitation item.
                while (true)
                {
                    if (U16(Position) == 0xFFFE && U16(Position + 2) == 0xE00D)
                    {
                        Header();
                        break;
                    }
                    if (Next().Undefined)
                        SkipSequence();
                }
            }
        }

        private (uint Tag, uint Length) Header()
        {
            if (Position + 8 > data.Length)
                throw Damaged();
            var header = ((uint)U16(Position) << 16 | U16(Position + 2), U32(Position + 4));
            Position += 8;
            return header;
        }

        private void Skip(uint length)
        {
            if (length > data.Length - Position)
                throw Damaged();
            Position += (int)length;
        }

        private ushort U16(int at) => at + 2 > data.Length ? throw Damaged()
            : bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));

        private uint U32(int at) => at + 4 > data.Length ? throw Damaged()
            : bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

        private static InvalidDataException Damaged() => new("The DICOM file is damaged.");
    }
}
