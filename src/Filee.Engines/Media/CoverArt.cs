// Album art for audio formats FFmpeg cannot write a picture stream into. Ogg (Vorbis, Opus) keeps the picture as a
// METADATA_BLOCK_PICTURE comment: a FLAC picture block in base64, which FFmpeg writes like any other tag when it comes
// from an ffmetadata file. Matroska audio keeps it as a cover.jpg / cover.png attachment.

using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Media;

/// <summary>Extra inputs that carry the source's cover into the output (see <see cref="CoverArt.PrepareAsync"/>).</summary>
/// <param name="MetadataFile">ffmpeg metadata file with the source's tags and the picture comment (Ogg).</param>
/// <param name="AttachmentFile">The picture to attach (Matroska).</param>
/// <param name="AttachmentMime">MIME type of <paramref name="AttachmentFile"/>.</param>
internal sealed record CoverExtras(string? MetadataFile, string? AttachmentFile, string? AttachmentMime);

internal static class CoverArt
{
    /// <summary>Targets whose cover is a METADATA_BLOCK_PICTURE comment.</summary>
    public static bool AsComment(string target) => target is "ogg" or "opus";

    /// <summary>Targets whose cover is an attached file.</summary>
    public static bool AsAttachment(string target) => target is "mka";

    /// <summary>
    /// Extracts the cover (stream <paramref name="stream"/>) of <paramref name="input"/> and prepares what
    /// <paramref name="target"/> needs to carry it. Null when the target doesn't need it or anything fails: the file is
    /// then converted without its cover rather than not at all.
    /// </summary>
    public static async Task<CoverExtras?> PrepareAsync(string ffmpeg, string input, int stream, string target, string workDirectory,
        CancellationToken cancellationToken)
    {
        if (!AsComment(target) && !AsAttachment(target))
            return null;
        try
        {
            var folder = Path.Combine(workDirectory, "cover-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var raw = Path.Combine(folder, "cover.bin");
            if (!await RunAsync(ffmpeg, ["-i", input, "-map", $"0:{stream}", "-c", "copy", "-frames:v", "1", "-f", "image2", raw], cancellationToken))
                return null;
            var image = await File.ReadAllBytesAsync(raw, cancellationToken);
            var mime = MimeOf(image);
            if (mime is null)
                return null;
            var picture = Path.Combine(folder, mime == "image/png" ? "cover.png" : "cover.jpg");
            File.Move(raw, picture);

            if (AsAttachment(target))
                return new CoverExtras(null, picture, mime);

            // The source's own tags (title, artist, …) go into the same file: the output takes its tags from it.
            var metadata = Path.Combine(folder, "metadata.txt");
            if (!await RunAsync(ffmpeg, ["-i", input, "-f", "ffmetadata", metadata], cancellationToken))
                return null;
            var info = new MagickImageInfo(image);
            var line = "METADATA_BLOCK_PICTURE=" + Escape(PictureBlock(image, mime, (int)info.Width, (int)info.Height));
            var lines = (await File.ReadAllLinesAsync(metadata, cancellationToken)).ToList();
            lines.Insert(Math.Min(1, lines.Count), line); // global tags come right after ";FFMETADATA1"
            await File.WriteAllTextAsync(metadata, string.Join('\n', lines) + "\n", new UTF8Encoding(false), cancellationToken);
            return new CoverExtras(metadata, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MagickException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A FLAC METADATA_BLOCK_PICTURE (front cover) in base64, as Ogg comments carry it: big-endian picture type,
    /// MIME type, description, width, height, colour depth, palette size and the image itself.
    /// </summary>
    internal static string PictureBlock(byte[] image, string mime, int width, int height)
    {
        var mimeBytes = Encoding.ASCII.GetBytes(mime);
        var block = new byte[4 * 8 + mimeBytes.Length + image.Length];
        var at = 0;
        void Int(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(at), value);
            at += 4;
        }
        Int(3); // front cover
        Int(mimeBytes.Length);
        mimeBytes.CopyTo(block, at);
        at += mimeBytes.Length;
        Int(0); // no description
        Int(width);
        Int(height);
        Int(24); // colour depth
        Int(0); // not indexed
        Int(image.Length);
        image.CopyTo(block, at);
        return Convert.ToBase64String(block);
    }

    /// <summary>ffmetadata escaping: '=', ';', '#', '\' and line breaks get a backslash.</summary>
    internal static string Escape(string value)
    {
        var text = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n')
                text.Append('\\');
            text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>JPEG or PNG by their first bytes; null for anything else.</summary>
    internal static string? MimeOf(byte[] image) =>
        image.Length > 4 && image[0] == 0xFF && image[1] == 0xD8 ? "image/jpeg"
        : image.Length > 8 && image[0] == 0x89 && image[1] == (byte)'P' && image[2] == (byte)'N' && image[3] == (byte)'G' ? "image/png"
        : null;

    private static async Task<bool> RunAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await FfmpegRunner.RunAsync(ffmpeg, [.. MediaEncoding.Common, .. arguments], null, cancellationToken);
        return result.ExitCode == 0;
    }
}
