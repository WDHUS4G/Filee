// Android Motion Photos: where the video saved with a camera picture is, read from the picture's own metadata, and new
// ones written the way a Pixel writes them.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Media;

/// <summary>
/// Google Motion Photo 1.0 JPEGs (Pixel, Samsung and others: an XMP container directory, the video appended), legacy
/// MicroVideo JPEGs (an offset from the end of the file) and HEIC / AVIF pictures with the video in an <c>mpvd</c> box.
/// </summary>
internal static class MotionPhoto
{
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private const string Camera = "http://ns.google.com/photos/1.0/camera/";
    private const string Container = "http://ns.google.com/photos/1.0/container/";
    private const string Item = "http://ns.google.com/photos/1.0/container/item/";

    /// <summary>JPEG metadata comes before the picture data; the walk stops there, and never goes further than this.</summary>
    private const int MaxMetadataBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Writes a Motion Photo 1.0 JPEG to <paramref name="output"/>: <paramref name="jpeg"/> with the Motion Photo XMP
    /// after its JFIF and Exif segments (JFIF must come first), then the MP4 <paramref name="video"/> appended, ending
    /// the file. The XMP is laid out as a Pixel writes it (<c>GCamera:MotionPhoto</c> 1, version 1, the still's
    /// presentation time; a container directory of the primary JPEG and the video with its length and no padding),
    /// plus the older MicroVideo properties (the same length as an offset from the end) for older Google Photos.
    /// </summary>
    public static async Task WriteAsync(byte[] jpeg, string video, string output, long presentationMicroseconds, CancellationToken ct)
    {
        var length = new FileInfo(video).Length;
        var payload = Xmp(length, presentationMicroseconds);
        var at = XmpPosition(jpeg);
        await using var target = File.Create(output);
        await target.WriteAsync(jpeg.AsMemory(0, at), ct);
        var header = new byte[4];
        header[0] = 0xff;
        header[1] = 0xe1;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), checked((ushort)(payload.Length + 2)));
        await target.WriteAsync(header, ct);
        await target.WriteAsync(payload, ct);
        await target.WriteAsync(jpeg.AsMemory(at), ct);
        await using var movie = File.OpenRead(video);
        await MovieBoxes.CopyRangeAsync(movie, target, length, ct);
    }

    /// <summary>The XMP segment's payload: the XMP signature, then the packet.</summary>
    internal static byte[] Xmp(long videoLength, long presentationMicroseconds)
    {
        var length = videoLength.ToString(CultureInfo.InvariantCulture);
        var time = presentationMicroseconds.ToString(CultureInfo.InvariantCulture);
        var xml = $"""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about=""
                    xmlns:GCamera="{Camera}"
                    xmlns:Container="{Container}"
                    xmlns:Item="{Item}"
                  GCamera:MotionPhoto="1"
                  GCamera:MotionPhotoVersion="1"
                  GCamera:MotionPhotoPresentationTimestampUs="{time}"
                  GCamera:MicroVideo="1"
                  GCamera:MicroVideoVersion="1"
                  GCamera:MicroVideoOffset="{length}"
                  GCamera:MicroVideoPresentationTimestampUs="{time}">
                  <Container:Directory>
                    <rdf:Seq>
                      <rdf:li rdf:parseType="Resource">
                        <Container:Item
                          Item:Semantic="Primary"
                          Item:Mime="image/jpeg"/>
                      </rdf:li>
                      <rdf:li rdf:parseType="Resource">
                        <Container:Item
                          Item:Mime="video/mp4"
                          Item:Semantic="MotionPhoto"
                          Item:Length="{length}"
                          Item:Padding="0"/>
                      </rdf:li>
                    </rdf:Seq>
                  </Container:Directory>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        return [.. XmpHeader, .. Encoding.UTF8.GetBytes(xml)];
    }

    /// <summary>Where the XMP segment goes: after the start marker and the JFIF (APP0) and Exif (APP1) segments.</summary>
    internal static int XmpPosition(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("The Motion Photo's picture is not a JPEG.");
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xff)
        {
            var marker = jpeg[at + 1];
            var exif = marker == 0xe1 && jpeg.AsSpan(at + 4).StartsWith("Exif\0"u8);
            if (marker != 0xe0 && !exif)
                break;
            var end = at + 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
            if (end > jpeg.Length)
                throw new InvalidDataException("Invalid JPEG segment length.");
            at = end;
        }
        return at;
    }

    /// <summary>
    /// Finds the declared video, never by searching the picture's bytes for an MP4 signature. Null for a picture
    /// without one.
    /// </summary>
    /// <exception cref="InvalidDataException">The picture says it has a video, but it isn't there (stale metadata).</exception>
    /// <exception cref="XmlException">The XMP can't be read.</exception>
    public static (long Offset, long Length)? FindVideo(Stream file)
    {
        file.Position = 0;
        if (file.ReadByte() == 0xff && file.ReadByte() == 0xd8)
            return FindJpegVideo(file);
        file.Position = 0;
        while (file.Position < file.Length)
        {
            var box = MovieBoxes.ReadHeader(file);
            if (box.Type == "mpvd")
            {
                // Samsung writes its own trailer after the box, so the box need not end the file.
                if (box.Length <= box.HeaderLength)
                    throw new InvalidDataException("Invalid Motion Photo video box.");
                return Checked(file, box.Start + box.HeaderLength, box.Length - box.HeaderLength);
            }
            file.Position = box.End;
        }
        return null;
    }

    private static (long Offset, long Length)? FindJpegVideo(Stream file)
    {
        while (file.Position < Math.Min(file.Length, MaxMetadataBytes))
        {
            if (file.ReadByte() != 0xff)
                throw new InvalidDataException("Invalid JPEG marker.");
            int marker;
            do { marker = file.ReadByte(); } while (marker == 0xff);
            if (marker is 0xda or 0xd9)
                return null;
            if (marker < 0)
                throw new EndOfStreamException();
            var size = new byte[2];
            file.ReadExactly(size);
            var length = BinaryPrimitives.ReadUInt16BigEndian(size) - 2;
            if (length < 0 || length > file.Length - file.Position)
                throw new InvalidDataException("Invalid JPEG segment length.");
            if (marker != 0xe1)
            {
                file.Position += length;
                continue;
            }
            var data = new byte[length];
            file.ReadExactly(data);
            if (!data.AsSpan().StartsWith(XmpHeader) || VideoLength(data) is not { } videoLength)
                continue;
            if (videoLength == 0)
                return null;
            // The video ends the file; Samsung's own trailer after it is cut off by Checked.
            var start = file.Length - videoLength;
            if (videoLength < 16 || start < file.Position)
                throw new InvalidDataException("Motion Photo video length is outside the file.");
            return Checked(file, start, videoLength);
        }
        return null;
    }

    /// <summary>
    /// The length from the end of the file that the XMP gives the video, 0 for a picture marked as not moving (a
    /// motion photo whose video was switched off), or null
    /// when this XMP packet says nothing about motion. Properties may be attributes or elements.
    /// </summary>
    private static long? VideoLength(byte[] data)
    {
        // Some writers pad the packet with NUL bytes after its end, which XML doesn't allow.
        var end = Math.Max(XmpHeader.Length, Array.LastIndexOf(data, (byte)'>') + 1);
        using var reader = XmlReader.Create(new MemoryStream(data, XmpHeader.Length, end - XmpHeader.Length),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65535 });
        var xml = XDocument.Load(reader);
        string? Property(XName name) =>
            xml.Descendants().Attributes(name).FirstOrDefault()?.Value ?? xml.Descendants(name).FirstOrDefault()?.Value;
        static string? Field(XElement item, string name) =>
            item.Attribute(XName.Get(name, Item))?.Value ?? item.Element(XName.Get(name, Item))?.Value;

        if (Property(XName.Get("MotionPhoto", Camera)) is { } motion)
        {
            if (motion.Trim() != "1")
                return 0;
            // The video is the last item of the directory (a Pixel lists a gain map before it).
            var last = xml.Descendants(XName.Get("Item", Container)).LastOrDefault();
            if (last is null || Field(last, "Semantic") != "MotionPhoto"
                || Field(last, "Mime") is not ("video/mp4" or "video/quicktime")
                || !long.TryParse(Field(last, "Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                throw new InvalidDataException("Motion Photo has no valid video directory entry.");
            return length;
        }
        // MicroVideo (Motion Photo before 1.0): an offset back from the end of the file.
        if (Property(XName.Get("MicroVideo", Camera))?.Trim() == "1")
        {
            return long.TryParse(Property(XName.Get("MicroVideoOffset", Camera)), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                ? offset
                : throw new InvalidDataException("MicroVideo has no valid offset.");
        }
        return null;
    }

    /// <summary>
    /// The video at <paramref name="start"/>, up to the end of its last whole box: an MP4 must start there (not stale
    /// XMP left by an editor that rewrote the picture) and hold its movie header. Anything after it (Samsung's SEF
    /// trailer) is left out.
    /// </summary>
    private static (long Offset, long Length) Checked(Stream file, long start, long length)
    {
        var end = start + length;
        var at = start;
        var movie = false;
        Span<byte> header = stackalloc byte[16];
        while (end - at >= 8)
        {
            file.Position = at;
            file.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header[4..8]);
            if (size == 1 && end - at >= 16)
            {
                file.ReadExactly(header[8..16]);
                var large = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                size = large > long.MaxValue ? -1 : (long)large;
            }
            else if (size == 0)
                size = end - at;
            if (size < 8 || size > end - at || (at == start && type != "ftyp"))
                break;
            movie |= type == "moov";
            at += size;
        }
        if (at == start)
            throw new InvalidDataException("The picture no longer contains its declared Motion Photo video.");
        if (!movie)
            throw new InvalidDataException("The Motion Photo video has no movie header.");
        return (start, at - start);
    }
}
