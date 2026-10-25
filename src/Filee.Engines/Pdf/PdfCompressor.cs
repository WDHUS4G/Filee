// Makes a PDF smaller while it stays a PDF (PDFsharp + Magick.NET). Pictures drawn at more than the level's resolution
// are scaled down to it, photos are saved as JPEG at the level's quality and pictures with few colours (charts,
// screenshots, logos) losslessly; every other stream is deflated at the highest level; page thumbnails, other
// applications' private data and, when the preset drops metadata, the XMP metadata of pages and pictures are left out
// (PDFsharp writes the document's own XMP from its document information on every save). Text, fonts and vector
// drawings stay as they are. A picture is changed only when the result is smaller.

using System.IO.Compression;
using Filee.Core.Presets;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>Shrinks the pictures and streams of a PDF document in place (before it is saved).</summary>
internal static class PdfCompressor
{
    /// <summary>
    /// Pictures are scaled down only when they are drawn at more than this many times the level's resolution, like
    /// Ghostscript does: a picture just above it would lose sharpness for little saving.
    /// </summary>
    private const double DownsampleThreshold = 1.5;

    /// <summary>A picture drawn tiny (or with a transformation read wrong) never gets smaller than this, in pixels.</summary>
    private const int MinimumSide = 8;

    /// <summary>A JPEG that keeps its size is encoded again only when that saves at least this share of it.</summary>
    private const double MinimumJpegSaving = 0.1;

    /// <summary>Colours up to which a picture is a graphic (kept lossless) rather than a photo (JPEG).</summary>
    private const int GraphicColors = 256;

    /// <summary>The same for gray pictures, which never have more than 256 shades: a gray photo has many of them.</summary>
    private const int GraphicGrays = 32;

    /// <summary>Resolution pictures are scaled down to (dpi) and JPEG quality, per level.</summary>
    public static (int Dpi, int Quality) Settings(PdfCompression level) => level switch
    {
        PdfCompression.Screen => (72, 55),
        PdfCompression.Print => (300, 85),
        _ => (150, 65),
    };

    /// <summary>Shrinks <paramref name="document"/>; save it afterwards.</summary>
    /// <param name="keepMetadata">False drops the XMP metadata of pages, pictures and forms.</param>
    public static void Compress(PdfDocument document, PdfCompression level, bool keepMetadata, CancellationToken cancellationToken)
    {
        var (dpi, quality) = Settings(level);
        var done = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var (image, use) in PdfPictureUses.Find(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (done.Add(image))
                ShrinkPicture(document, image, use, dpi, quality, done);
        }

        MergeDuplicatePictures(document);

        foreach (var page in document.Pages)
        {
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
        }
        document.Internals.Catalog.Elements.Remove("/PieceInfo");
        // PDFsharp writes the document's XMP afresh from its document information when it saves (as for every PDF it
        // writes); the old packet would stay in the file unreferenced, so it goes whatever the preset says.
        document.Internals.Catalog.Elements.Remove("/Metadata");

        foreach (var item in document.Internals.GetAllObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not PdfDictionary dictionary)
                continue;
            if (!keepMetadata && dictionary.Elements.ContainsKey("/Metadata"))
                dictionary.Elements.Remove("/Metadata");
            if (dictionary.Stream is not null)
                Deflate(dictionary);
        }

        document.Options.CompressContentStreams = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.Layout = PdfWriterLayout.Compact;
    }

    /// <summary>Scales a picture down and saves it as JPEG or Flate, when that's smaller. Soft masks follow it.</summary>
    private static void ShrinkPicture(PdfDocument document, PdfDictionary image, PictureUse use, int dpi, int quality, HashSet<PdfDictionary> done)
    {
        if (Decode(image) is not { } picture)
            return;
        using var pixels = picture.Image;
        var width = (int)pixels.Width;
        var height = (int)pixels.Height;

        // Pixels needed to show the picture at the target resolution where it is drawn largest.
        var scale = Math.Max(use.WidthInches * dpi / width, use.HeightInches * dpi / height);
        var downsample = scale * DownsampleThreshold < 1;
        var newWidth = downsample ? Math.Clamp((int)Math.Round(width * scale), Math.Min(MinimumSide, width), width) : width;
        var newHeight = downsample ? Math.Clamp((int)Math.Round(height * scale), Math.Min(MinimumSide, height), height) : height;
        downsample = newWidth < width || newHeight < height;
        if (downsample)
            pixels.Resize(new MagickGeometry((uint)newWidth, (uint)newHeight) { IgnoreAspectRatio = true });

        var original = image.Stream.Value.Length;
        // A JPEG that only gets a lower quality is worth it only for a clear saving: it loses detail once more.
        var limit = !downsample && picture.WasJpeg ? (int)(original * (1 - MinimumJpegSaving)) : original;
        var graphic = !picture.WasJpeg && pixels.TotalColors <= (picture.Components == 1 ? GraphicGrays : GraphicColors);
        var encoded = picture.Components == 4 || graphic
            ? EncodeFlate(pixels, picture.Components)
            : EncodeJpeg(pixels, picture.Components, quality);
        if (encoded is null || encoded.Value.Data.Length >= limit)
            return;

        image.Stream.Value = encoded.Value.Data;
        image.Elements.SetName("/Filter", encoded.Value.Filter);
        if (encoded.Value.DecodeParms is { } parms)
            image.Elements["/DecodeParms"] = Parms(document, parms);
        else
            image.Elements.Remove("/DecodeParms");
        image.Elements.SetInteger("/Width", newWidth);
        image.Elements.SetInteger("/Height", newHeight);
        image.Elements.SetInteger("/BitsPerComponent", 8);

        // The soft mask (transparency) may have its own size: scaled by as much as the picture, and kept lossless.
        if (downsample && Resolve(image.Elements["/SMask"]) is PdfDictionary mask && done.Add(mask))
            ShrinkMask(document, mask, (double)newWidth / width, (double)newHeight / height);
    }

    private static void ShrinkMask(PdfDocument document, PdfDictionary mask, double scaleX, double scaleY)
    {
        if (Decode(mask) is not { Components: 1, WasJpeg: false } picture)
            return;
        using var pixels = picture.Image;
        var width = (int)Math.Max(1, Math.Round(pixels.Width * scaleX));
        var height = (int)Math.Max(1, Math.Round(pixels.Height * scaleY));
        pixels.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        if (EncodeFlate(pixels, 1) is not { } encoded || encoded.Data.Length >= mask.Stream.Value.Length)
            return;
        mask.Stream.Value = encoded.Data;
        mask.Elements.SetName("/Filter", encoded.Filter);
        mask.Elements["/DecodeParms"] = Parms(document, encoded.DecodeParms!.Value);
        mask.Elements.SetInteger("/Width", width);
        mask.Elements.SetInteger("/Height", height);
        mask.Elements.SetInteger("/BitsPerComponent", 8);
    }

    /// <summary>
    /// Pictures stored more than once with the same data and settings (a logo embedded again on every page) are kept
    /// once: every reference to a copy points to the first. Soft masks first, so the pictures using them compare equal.
    /// </summary>
    private static void MergeDuplicatePictures(PdfDocument document)
    {
        var images = document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Stream is not null && d.Elements.GetName("/Subtype") == "/Image" && d.Reference is not null)
            .ToList();
        foreach (var pass in new[] { images.Where(Plain).ToList(), images.Where(i => !Plain(i)).ToList() })
        {
            var first = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
            var copies = new Dictionary<PdfDictionary, PdfReference>(ReferenceEqualityComparer.Instance);
            foreach (var image in pass)
            {
                var key = string.Join(" ", image.Elements.Keys.Where(k => k != "/Length").Order(StringComparer.Ordinal)
                    .Select(k => k + " " + image.Elements[k])) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Stream.Value));
                if (!first.TryAdd(key, image))
                    copies[image] = first[key].Reference!;
            }
            if (copies.Count == 0)
                continue;
            foreach (var item in document.Internals.GetAllObjects())
            {
                if (item is PdfDictionary or PdfArray)
                    Redirect(item, copies, depth: 0);
            }
        }

        static bool Plain(PdfDictionary image) => !image.Elements.ContainsKey("/SMask") && !image.Elements.ContainsKey("/Mask");
    }

    /// <summary>Points references to the copies in <paramref name="copies"/> to the kept picture, in direct objects too.</summary>
    private static void Redirect(PdfItem container, Dictionary<PdfDictionary, PdfReference> copies, int depth)
    {
        if (depth > 32)
            return;
        if (container is PdfDictionary dictionary)
        {
            foreach (var key in dictionary.Elements.Keys.ToList())
            {
                var value = dictionary.Elements[key];
                if (value is PdfReference { Value: PdfDictionary target } && copies.TryGetValue(target, out var kept))
                    dictionary.Elements[key] = kept;
                else if (value is PdfDictionary { Reference: null } or PdfArray { Reference: null })
                    Redirect(value, copies, depth + 1);
            }
        }
        else if (container is PdfArray array)
        {
            for (var i = 0; i < array.Elements.Count; i++)
            {
                var value = array.Elements[i];
                if (value is PdfReference { Value: PdfDictionary target } && copies.TryGetValue(target, out var kept))
                    array.Elements[i] = kept;
                else if (value is PdfDictionary { Reference: null } or PdfArray { Reference: null })
                    Redirect(value, copies, depth + 1);
            }
        }
    }

    private static PdfDictionary Parms(PdfDocument document, (int Colors, int Columns) parms)
    {
        var dictionary = new PdfDictionary(document);
        dictionary.Elements.SetInteger("/Predictor", 15);
        dictionary.Elements.SetInteger("/Colors", parms.Colors);
        dictionary.Elements.SetInteger("/BitsPerComponent", 8);
        dictionary.Elements.SetInteger("/Columns", parms.Columns);
        return dictionary;
    }

    private sealed record DecodedPicture(MagickImage Image, int Components, bool WasJpeg);

    /// <summary>
    /// The pixels of a picture Filee can encode again without changing how it looks: 8 bits per component, gray, RGB or
    /// CMYK (CMYK not from JPEG: Adobe's inverted CMYK JPEGs are easy to get wrong), stored as JPEG, Flate or
    /// uncompressed. Null for everything else (JBIG2, CCITT, JPEG 2000, 1-bit, indexed, spot colours, colour-key
    /// masks), which stays as it is.
    /// </summary>
    private static DecodedPicture? Decode(PdfDictionary image)
    {
        var elements = image.Elements;
        if (elements.GetName("/Subtype") != "/Image" || elements.GetBoolean("/ImageMask") || elements.GetInteger("/BitsPerComponent") != 8
            || Resolve(elements["/Mask"]) is PdfArray || elements.ContainsKey("/F"))
            return null;
        var width = elements.GetInteger("/Width");
        var height = elements.GetInteger("/Height");
        var components = Components(Resolve(elements["/ColorSpace"]));
        if (width <= 0 || height <= 0 || components == 0 || (long)width * height > 100_000_000)
            return null;

        var filters = Filters(image);
        var data = image.Stream.Value;
        try
        {
            if (filters is ["/DCTDecode"])
            {
                // ColorTransform overrides how the JPEG's colours are read: rare, so left alone.
                if (components == 4 || Parms(image) is { } dct && dct.Elements.ContainsKey("/ColorTransform"))
                    return null;
                var jpeg = new MagickImage(data, MagickFormat.Jpeg);
                var gray = jpeg.ColorSpace == ColorSpace.Gray || jpeg.ChannelCount == 1;
                if (jpeg.Width != width || jpeg.Height != height || gray != (components == 1) || jpeg.ColorSpace == ColorSpace.CMYK)
                {
                    jpeg.Dispose();
                    return null;
                }
                return new DecodedPicture(jpeg, components, WasJpeg: true);
            }

            if (filters is not ([] or ["/FlateDecode"] or ["/RunLengthDecode"]))
                return null;
            var raw = filters switch
            {
                [] => data,
                ["/RunLengthDecode"] => UndoRunLength(data),
                _ => Inflate(data),
            };
            if (filters is ["/FlateDecode"] && Parms(image) is { } flate && flate.Elements.GetInteger("/Predictor") > 1)
            {
                // PNG prediction of whole pixels only; TIFF prediction (2) is rare in pictures.
                var parms = flate.Elements;
                var colors = parms.ContainsKey("/Colors") ? parms.GetInteger("/Colors") : 1;
                var columns = parms.ContainsKey("/Columns") ? parms.GetInteger("/Columns") : 1;
                var bits = parms.ContainsKey("/BitsPerComponent") ? parms.GetInteger("/BitsPerComponent") : 8;
                if (parms.GetInteger("/Predictor") < 10 || colors != components || columns != width || bits != 8
                    || UndoPngPredictor(raw, width * components, height, components) is not { } unpredicted)
                    return null;
                raw = unpredicted;
            }
            var size = width * height * components;
            if (raw.Length < size)
                return null;
            var settings = new MagickReadSettings
            {
                Width = (uint)width,
                Height = (uint)height,
                Depth = 8,
                Format = components switch { 1 => MagickFormat.Gray, 3 => MagickFormat.Rgb, _ => MagickFormat.Cmyk },
            };
            return new DecodedPicture(new MagickImage(raw.AsSpan(0, size), settings), components, WasJpeg: false);
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException)
        {
            return null; // damaged or unusual data: the picture stays as it is
        }
    }

    private static (byte[] Data, string Filter, (int Colors, int Columns)? DecodeParms)? EncodeJpeg(MagickImage pixels, int components, int quality)
    {
        pixels.Strip();
        pixels.Quality = (uint)quality;
        pixels.ColorType = components == 1 ? ColorType.Grayscale : ColorType.TrueColor;
        var data = pixels.ToByteArray(MagickFormat.Jpeg);
        // Exactly what the image dictionary says, or the picture would come out wrong: ImageMagick could write a
        // gray-looking colour picture with one component.
        return JpegFrame(data) is var (w, h, n) && w == pixels.Width && h == pixels.Height && n == components
            ? (data, "/DCTDecode", null)
            : null;
    }

    private static (byte[] Data, string Filter, (int Colors, int Columns)? DecodeParms)? EncodeFlate(MagickImage pixels, int components)
    {
        pixels.Depth = 8;
        var raw = pixels.ToByteArray(components switch { 1 => MagickFormat.Gray, 3 => MagickFormat.Rgb, _ => MagickFormat.Cmyk });
        var stride = (int)pixels.Width * components;
        if (raw.Length != stride * (int)pixels.Height)
            return null;
        return (Deflate(PngPredict(raw, stride, (int)pixels.Height, components)), "/FlateDecode", (components, (int)pixels.Width));
    }

    /// <summary>Deflates an uncompressed stream, or deflates a Flate stream again at the highest level, when smaller.</summary>
    private static void Deflate(PdfDictionary dictionary)
    {
        var type = dictionary.Elements.GetName("/Type");
        // XMP stays readable for tools that search files for it; object and cross-reference streams are PDFsharp's.
        if (type is "/Metadata" or "/ObjStm" or "/XRef" || dictionary.Elements.ContainsKey("/F"))
            return;
        var filters = Filters(dictionary);
        var data = dictionary.Stream.Value;
        if (data.Length < 64)
            return;
        try
        {
            if (filters.Length == 0)
            {
                var packed = Deflate(data);
                if (packed.Length >= data.Length)
                    return;
                dictionary.Stream.Value = packed;
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
            else if (filters is ["/FlateDecode"])
            {
                // The data stays the same (predictor included), only packed tighter.
                var packed = Deflate(Inflate(data));
                if (packed.Length < data.Length)
                    dictionary.Stream.Value = packed;
            }
            else if (filters is ["/RunLengthDecode"])
            {
                var packed = Deflate(UndoRunLength(data));
                if (packed.Length >= data.Length)
                    return;
                dictionary.Stream.Value = packed;
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
        }
        catch (InvalidDataException)
        {
            // damaged stream: left as it is
        }
    }

    private static string[] Filters(PdfDictionary dictionary) => Resolve(dictionary.Elements["/Filter"]) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Elements.Select(e => Resolve(e) is PdfName n ? n.Value : "?")],
        _ => [],
    };

    private static PdfDictionary? Parms(PdfDictionary dictionary) => Resolve(dictionary.Elements["/DecodeParms"]) switch
    {
        PdfDictionary parms => parms,
        PdfArray { Elements.Count: 1 } array => Resolve(array.Elements[0]) as PdfDictionary,
        _ => null,
    };

    /// <summary>Components of a colour space Filee re-encodes (gray, RGB, CMYK and their calibrated / ICC forms), else 0.</summary>
    private static int Components(PdfItem? colorSpace) => colorSpace switch
    {
        PdfName { Value: "/DeviceGray" } => 1,
        PdfName { Value: "/DeviceRGB" } => 3,
        PdfName { Value: "/DeviceCMYK" } => 4,
        PdfArray { Elements.Count: > 0 } array => (Resolve(array.Elements[0]) as PdfName)?.Value switch
        {
            "/CalGray" => 1,
            "/CalRGB" => 3,
            "/ICCBased" when array.Elements.Count > 1 && Resolve(array.Elements[1]) is PdfDictionary icc
                => icc.Elements.GetInteger("/N") is var n and (1 or 3 or 4) ? n : 0,
            _ => 0,
        },
        _ => 0,
    };

    /// <summary>The object a reference points to; a name stored as an object of its own (ImageMagick does) as a name.</summary>
    internal static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference
        ? reference.Value is PdfNameObject name ? new PdfName(name.Value) : reference.Value
        : item;

    private static byte[] Inflate(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// RunLengthDecode (ImageMagick writes pictures with it): a length byte n &lt; 128 copies the next n + 1 bytes, n &gt; 128
    /// repeats the next byte 257 - n times, 128 ends the data.
    /// </summary>
    private static byte[] UndoRunLength(byte[] data)
    {
        using var output = new MemoryStream(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
                break;
            if (length < 128)
            {
                var count = Math.Min(length + 1, data.Length - i);
                output.Write(data, i, count);
                i += count;
            }
            else if (i < data.Length)
            {
                var value = data[i++];
                for (var n = 0; n < 257 - length; n++)
                    output.WriteByte(value);
            }
        }
        return output.ToArray();
    }

    /// <summary>Reverses PNG prediction (/Predictor 10-15): each row starts with its filter type. Null when damaged.</summary>
    private static byte[]? UndoPngPredictor(byte[] data, int stride, int rows, int bpp)
    {
        if (data.Length < (long)(stride + 1) * rows)
            return null;
        var result = new byte[stride * rows];
        var none = new byte[stride];
        for (var r = 0; r < rows; r++)
        {
            var type = data[r * (stride + 1)];
            var source = data.AsSpan(r * (stride + 1) + 1, stride);
            var row = result.AsSpan(r * stride, stride);
            ReadOnlySpan<byte> up = r == 0 ? none : result.AsSpan((r - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0, b = up[x], c = x >= bpp ? up[x - bpp] : 0;
                int predicted;
                switch (type)
                {
                    case 0: predicted = 0; break;
                    case 1: predicted = a; break;
                    case 2: predicted = b; break;
                    case 3: predicted = (a + b) / 2; break;
                    case 4: predicted = Paeth(a, b, c); break;
                    default: return null;
                }
                row[x] = (byte)(source[x] + predicted);
            }
        }
        return result;
    }

    /// <summary>PNG-predicts rows (/Predictor 15), each with the filter that leaves the smallest values.</summary>
    private static byte[] PngPredict(byte[] raw, int stride, int rows, int bpp)
    {
        var output = new byte[(stride + 1) * rows];
        var candidates = new byte[5][];
        for (var t = 0; t < candidates.Length; t++)
            candidates[t] = new byte[stride];
        var none = new byte[stride];
        for (var r = 0; r < rows; r++)
        {
            ReadOnlySpan<byte> row = raw.AsSpan(r * stride, stride);
            ReadOnlySpan<byte> up = r == 0 ? none : raw.AsSpan((r - 1) * stride, stride);
            var best = 0;
            var bestSum = long.MaxValue;
            for (var type = 0; type < candidates.Length; type++)
            {
                var filtered = candidates[type];
                long sum = 0;
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? row[x - bpp] : 0, b = up[x], c = x >= bpp ? up[x - bpp] : 0;
                    var predicted = type switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                    var value = (byte)(row[x] - predicted);
                    filtered[x] = value;
                    sum += value < 128 ? value : 256 - value;
                }
                if (sum < bestSum)
                {
                    bestSum = sum;
                    best = type;
                }
            }
            output[r * (stride + 1)] = (byte)best;
            candidates[best].CopyTo(output.AsSpan(r * (stride + 1) + 1));
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Width, height and components of a JPEG's frame header, or null.</summary>
    private static (int Width, int Height, int Components)? JpegFrame(byte[] jpeg)
    {
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
                return null;
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6], jpeg[i + 9]);
            i += 2 + length;
        }
        return null;
    }
}
