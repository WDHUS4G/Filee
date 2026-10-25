// FITS images (Flexible Image Transport System: telescope cameras, capture and stacking software), read by Filee itself
// from the FITS standard 4.0: 2880-byte blocks, a header of 80-character cards, big-endian data. ImageMagick's Q8 build
// would cut 16- and 32-bit data down to 8 bits and knows nothing of Bayer patterns, so the values are read here as they
// are (BZERO + BSCALE × stored) for FitsRendering to stretch, debayer or write into a TIFF of the file's own depth.
// The first HDU holding a picture is read: the primary one, or else the first IMAGE extension. Tile-compressed images
// (fpack, ".fz") are not read.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Filee.Engines.Magick;

/// <summary>
/// A FITS picture: the physical values of its planes in the order the file stores them, and what the header says
/// about them.
/// </summary>
internal sealed class FitsImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>One plane (mono, or the mosaic of a colour camera) or three (red, green, blue).</summary>
    public required float[][] Planes { get; init; }

    /// <summary>BITPIX: 8, 16, 32 or 64 for integers, -32 or -64 for floating point.</summary>
    public required int Bitpix { get; init; }

    /// <summary>Smallest and largest finite value of all planes (0 and 0 when there is none).</summary>
    public required float Min { get; init; }
    public required float Max { get; init; }

    /// <summary>Every value is a whole number (integer data with a whole BZERO and BSCALE and no BLANK pixels).</summary>
    public required bool Integral { get; init; }

    /// <summary>The colour filter pattern at the first stored pixel (RGGB, BGGR, GRBG or GBRG), or null.</summary>
    public string? BayerPattern { get; init; }

    /// <summary>XBAYROFF / YBAYROFF: where the pattern starts, 0 or 1.</summary>
    public int BayerOffsetX { get; init; }
    public int BayerOffsetY { get; init; }

    /// <summary>ROWORDER = 'BOTTOM-UP': the first stored row is the bottom of the picture.</summary>
    public bool BottomUp { get; init; }
}

/// <summary>Reads the picture of a FITS file.</summary>
internal static class Fits
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    /// <summary>Most values one picture may have (a 1 GB array of floats, about a 16 000 × 16 000 mosaic).</summary>
    private const long MaxSamples = 256L * 1024 * 1024;

    /// <summary>Header blocks one HDU may have before its END card (36 cards each): far more than any real file.</summary>
    private const int MaxHeaderBlocks = 10_000;

    private static readonly string[] Patterns = ["RGGB", "BGGR", "GRBG", "GBRG"];

    /// <summary>Keywords that name the Bayer pattern, in the order they are looked for.</summary>
    private static readonly string[] PatternKeywords = ["BAYERPAT", "COLORTYP"];

    /// <exception cref="InvalidDataException">The file is not FITS, is damaged, or has no picture Filee reads.</exception>
    public static FitsImage Read(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Dictionary<string, string>? primary = null;
        var compressed = false;
        long position = 0;
        for (var hdu = 0; position < stream.Length; hdu++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = position;
            var header = ReadHeader(stream, hdu == 0);
            primary ??= header;
            var bitpix = (int)Integer(header, "BITPIX");
            if (bitpix is not (8 or 16 or 32 or 64 or -32 or -64))
                throw new InvalidDataException($"This FITS file has an unknown BITPIX ({bitpix}).");
            var naxis = Integer(header, "NAXIS");
            if (naxis is < 0 or > 999)
                throw new InvalidDataException("This FITS file is damaged (NAXIS).");
            var axes = new long[naxis];
            for (var i = 0; i < axes.Length; i++)
            {
                axes[i] = Integer(header, $"NAXIS{i + 1}");
                if (axes[i] < 0)
                    throw new InvalidDataException("This FITS file is damaged (a negative axis length).");
            }
            var dataStart = stream.Position;
            var extension = hdu == 0 ? null : Text(header, "XTENSION");
            if (axes.Length >= 2 && axes[0] > 0 && axes[1] > 0 && (hdu == 0 || extension == "IMAGE"))
                return ReadImage(stream, dataStart, header, primary, bitpix, axes, cancellationToken);
            if (extension == "BINTABLE" && Text(header, "ZIMAGE") == "T")
                compressed = true;

            // Skip the data: |BITPIX| / 8 × GCOUNT × (PCOUNT + NAXIS1 × … × NAXISn), padded to whole blocks.
            long elements = axes.Length == 0 ? 0 : 1;
            foreach (var axis in axes)
                elements = checked(elements * axis);
            var groups = hdu == 0 ? 1 : Optional(header, "GCOUNT", 1);
            var parameters = hdu == 0 ? 0 : Optional(header, "PCOUNT", 0);
            var bytes = elements == 0 && parameters == 0 ? 0 : checked(Math.Abs(bitpix) / 8 * groups * (parameters + elements));
            position = dataStart + (bytes + BlockSize - 1) / BlockSize * BlockSize;
        }
        throw new InvalidDataException(compressed
            ? "This FITS file is tile-compressed (fpack). Uncompress it first (funpack) and try again."
            : "This FITS file has no picture: it holds only tables or an empty header.");
    }

    private static FitsImage ReadImage(Stream stream, long dataStart, Dictionary<string, string> header,
        Dictionary<string, string> primary, int bitpix, long[] axes, CancellationToken cancellationToken)
    {
        var (width, height) = (axes[0], axes[1]);
        // A cube of three planes is red, green and blue; of any other depth, the first plane is read.
        var planeCount = axes.Length >= 3 && axes[2] == 3 && axes.Skip(3).All(a => a == 1) ? 3 : 1;
        if (width > MaxSamples || height > MaxSamples || width * height > MaxSamples / planeCount)
            throw new InvalidDataException($"This FITS picture is too large ({width} × {height}).");

        var bzero = Real(header, "BZERO", 0);
        var bscale = Real(header, "BSCALE", 1);
        long? blank = bitpix > 0 && header.ContainsKey("BLANK") ? Integer(header, "BLANK") : null;
        var bytesPerValue = Math.Abs(bitpix) / 8;
        var pixels = (int)(width * height);
        var row = new byte[(int)width * bytesPerValue];
        var planes = new float[planeCount][];
        var (min, max) = (float.PositiveInfinity, float.NegativeInfinity);
        var blanks = false;
        stream.Position = dataStart;
        for (var p = 0; p < planeCount; p++)
        {
            var plane = planes[p] = new float[pixels];
            for (var y = 0; y < height; y++)
            {
                if ((y & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                stream.ReadExactly(row);
                var values = plane.AsSpan(y * (int)width, (int)width);
                blanks |= Decode(row, values, bitpix, bzero, bscale, blank);
                foreach (var value in values)
                {
                    if (float.IsFinite(value))
                    {
                        if (value < min)
                            min = value;
                        if (value > max)
                            max = value;
                    }
                }
            }
        }
        if (min > max)
            (min, max) = (0, 0);

        string? pattern = null;
        if (planeCount == 1)
        {
            foreach (var keyword in PatternKeywords)
            {
                var text = (Text(header, keyword) ?? Text(primary, keyword))?.Trim().ToUpperInvariant();
                if (text is not null && Patterns.Contains(text))
                {
                    pattern = text;
                    break;
                }
            }
        }
        return new FitsImage
        {
            Width = (int)width,
            Height = (int)height,
            Planes = planes,
            Bitpix = bitpix,
            Min = min,
            Max = max,
            Integral = bitpix > 0 && bzero == Math.Floor(bzero) && bscale == Math.Floor(bscale) && !blanks,
            BayerPattern = pattern,
            BayerOffsetX = Parity(Optional(header, "XBAYROFF", Optional(primary, "XBAYROFF", 0))),
            BayerOffsetY = Parity(Optional(header, "YBAYROFF", Optional(primary, "YBAYROFF", 0))),
            BottomUp = (Text(header, "ROWORDER") ?? Text(primary, "ROWORDER"))?.Trim().ToUpperInvariant() == "BOTTOM-UP",
        };
    }

    /// <summary>
    /// One row of big-endian stored values as physical values (BZERO + BSCALE × stored); BLANK integers become NaN.
    /// Returns true if there was one.
    /// </summary>
    private static bool Decode(ReadOnlySpan<byte> row, Span<float> values, int bitpix, double bzero, double bscale, long? blank)
    {
        var blanks = false;
        var (hasBlank, blankValue) = (blank.HasValue, blank.GetValueOrDefault());
        float Scaled(long stored)
        {
            if (hasBlank && stored == blankValue)
            {
                blanks = true;
                return float.NaN;
            }
            return (float)(bzero + bscale * stored);
        }

        switch (bitpix)
        {
            case 8:
                for (var i = 0; i < values.Length; i++)
                    values[i] = Scaled(row[i]);
                break;
            case 16:
                for (var i = 0; i < values.Length; i++)
                    values[i] = Scaled(BinaryPrimitives.ReadInt16BigEndian(row[(i * 2)..]));
                break;
            case 32:
                for (var i = 0; i < values.Length; i++)
                    values[i] = Scaled(BinaryPrimitives.ReadInt32BigEndian(row[(i * 4)..]));
                break;
            case 64:
                for (var i = 0; i < values.Length; i++)
                    values[i] = Scaled(BinaryPrimitives.ReadInt64BigEndian(row[(i * 8)..]));
                break;
            case -32:
                for (var i = 0; i < values.Length; i++)
                    values[i] = (float)(bzero + bscale * BinaryPrimitives.ReadSingleBigEndian(row[(i * 4)..]));
                break;
            default:
                for (var i = 0; i < values.Length; i++)
                    values[i] = (float)(bzero + bscale * BinaryPrimitives.ReadDoubleBigEndian(row[(i * 8)..]));
                break;
        }
        return blanks;
    }

    private static int Parity(long value) => (int)(((value % 2) + 2) % 2);

    /// <summary>
    /// The keyword = value cards of one header, read up to its END card (the stream is left at the first data block).
    /// The first card of a file is SIMPLE = T; an extension's is XTENSION. Repeated keywords keep their first value.
    /// </summary>
    private static Dictionary<string, string> ReadHeader(Stream stream, bool primary)
    {
        var cards = new Dictionary<string, string>(StringComparer.Ordinal);
        var block = new byte[BlockSize];
        for (var b = 0; b < MaxHeaderBlocks; b++)
        {
            if (stream.ReadAtLeast(block, BlockSize, throwOnEndOfStream: false) < BlockSize)
                throw new InvalidDataException(b == 0 && primary ? "This is not a FITS file." : "This FITS file is cut short.");
            for (var c = 0; c < BlockSize / CardSize; c++)
            {
                var card = Encoding.ASCII.GetString(block, c * CardSize, CardSize);
                var keyword = card[..8].TrimEnd();
                if (b == 0 && c == 0 && keyword != (primary ? "SIMPLE" : "XTENSION"))
                    throw new InvalidDataException(primary ? "This is not a FITS file." : "This FITS file is damaged (an extension without XTENSION).");
                if (keyword == "END")
                    return cards;
                if (keyword.Length > 0 && card[8] == '=' && card[9] == ' ' && !cards.ContainsKey(keyword))
                    cards[keyword] = Value(card[10..]);
            }
        }
        throw new InvalidDataException("This FITS file is damaged (its header has no END).");
    }

    /// <summary>A card's value: a string between quotes ('' is a quote), else the text before the comment.</summary>
    private static string Value(string field)
    {
        var text = field.TrimStart();
        if (!text.StartsWith('\''))
        {
            var slash = text.IndexOf('/');
            return (slash < 0 ? text : text[..slash]).Trim();
        }
        var value = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i++;
                    continue;
                }
                break;
            }
            value.Append(text[i]);
        }
        // Trailing spaces of a string are not significant; leading ones are.
        return value.ToString().TrimEnd();
    }

    private static string? Text(Dictionary<string, string> header, string keyword) => header.GetValueOrDefault(keyword);

    private static double? Number(Dictionary<string, string> header, string keyword) =>
        header.TryGetValue(keyword, out var text)
        && double.TryParse(text.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    private static long Integer(Dictionary<string, string> header, string keyword) =>
        Number(header, keyword) is { } value && value == Math.Floor(value) && Math.Abs(value) < 9e15
            ? (long)value
            : throw new InvalidDataException($"This FITS file is damaged (no valid {keyword}).");

    private static long Optional(Dictionary<string, string> header, string keyword, long fallback) =>
        Number(header, keyword) is { } value && value == Math.Floor(value) && Math.Abs(value) < 9e15 ? (long)value : fallback;

    private static double Real(Dictionary<string, string> header, string keyword, double fallback) =>
        Number(header, keyword) ?? fallback;
}
