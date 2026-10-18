// Loads a font file of any supported container (TTF/OTF/TTC, WOFF, WOFF2, EOT), recognised by its content rather
// than its extension, and saves a font as TTF, OTF, WOFF, WOFF2 or EOT.

using System.Buffers.Binary;

namespace Filee.Engines.Fonts;

/// <summary>Reading and writing font files in every supported format.</summary>
internal static class FontFile
{
    /// <summary>Format ids (see FormatRegistry) this engine reads and writes.</summary>
    public static readonly string[] Formats = ["ttf", "otf", "woff", "woff2", "eot"];

    /// <summary>Decodes a font file, whatever its container.</summary>
    public static SfntFont Load(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data.Length >= 4)
        {
            switch (BinaryPrimitives.ReadUInt32BigEndian(data))
            {
                case Woff.Signature:
                    return Woff.Decode(data);
                case Woff2.Signature:
                    return Woff2.Decode(data, cancellationToken);
            }
        }
        if (SfntFont.IsSfnt(data))
            return SfntFont.Read(data);
        if (Eot.IsEot(data))
            return Eot.Decode(data);
        throw new InvalidDataException("This file is not a font Filee can read (TTF, OTF, WOFF, WOFF2 or EOT).");
    }

    /// <summary>
    /// Encodes the font as <paramref name="format"/>. TTF and EOT need TrueType outlines, so CFF outlines are
    /// converted first; OTF keeps whatever outlines the font has (TrueType-flavoured OpenType is valid OpenType).
    /// </summary>
    public static byte[] Save(SfntFont font, string format, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        switch (format)
        {
            case "ttf":
                return TrueTypeOutlines(font, cancellationToken, progress).Write();
            case "otf":
                return font.Write();
            case "woff":
                return Woff.Encode(font, cancellationToken);
            case "woff2":
                return Woff2.Encode(font, cancellationToken, progress);
            case "eot":
                // Internet Explorer, the only EOT consumer, renders EOT fonts with TrueType outlines only.
                return Eot.Encode(TrueTypeOutlines(font, cancellationToken, progress));
            default:
                throw new ArgumentException($"Unknown font format '{format}'.", nameof(format));
        }
    }

    /// <summary>The font itself when it has TrueType outlines, else a converted copy (CFF2 fails with a clear message).</summary>
    private static SfntFont TrueTypeOutlines(SfntFont font, CancellationToken cancellationToken, IProgress<double>? progress) =>
        font["glyf"] is null && (font["CFF "] is not null || font["CFF2"] is not null)
            ? CffToTrueType.Convert(font, cancellationToken, progress)
            : font;
}
