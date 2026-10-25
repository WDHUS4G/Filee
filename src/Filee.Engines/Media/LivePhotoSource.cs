// The video of a Live / Motion Photo, copied into the job's work directory: the photo, the MOV next to it and a LIVP
// are only read. Archive entry names are never used as paths.

using System.IO.Compression;
using System.Xml;
using Filee.Core.Conversion;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Media;

internal static class LivePhotoSource
{
    private const long MaxVideoBytes = 512L * 1024 * 1024;
    private const long MaxImageBytes = 64L * 1024 * 1024;

    /// <summary>A LIVP holds a photo and its MOV; anything much larger is not one.</summary>
    private const int MaxPackageEntries = 16;

    private static readonly string[] MovieExtensions = [".mov", ".mp4"];
    private static readonly string[] StillExtensions = [".jpg", ".jpeg", ".heic", ".heif"];

    /// <summary>
    /// Copies the video of <paramref name="input"/> (format <paramref name="from"/>) into
    /// <paramref name="workDirectory"/>: the video appended to an Android Motion Photo, the MOV of a LIVP, or the MOV
    /// next to an iPhone photo with the same name and content identifier.
    /// </summary>
    /// <returns>The copy, and the iPhone MOV it was copied from (null for the others): no output may replace it.</returns>
    /// <exception cref="ConversionRefusedException"><c>error.no_motion_photo</c>: the photo has no video Filee can read.</exception>
    public static async Task<(string Video, string? Companion)> ExtractAsync(string input, string from, string workDirectory,
        ILogger logger, CancellationToken ct)
    {
        // Stale or broken metadata, or a damaged LIVP: the reason goes to the log, the user gets the plain message.
        // Metadata of one kind that can't be read doesn't keep the other from being looked at.
        string? reason = null;
        try
        {
            if (from == "livp")
                return (await CopyPackageVideoAsync(input, workDirectory, logger, ct), null);
            if (await CopyEmbeddedAsync(input, workDirectory, ct) is { } embedded)
                return (embedded, null);
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            reason = ex.Message;
        }
        try
        {
            if (from is "jpg" or "heic" && PairedMovie(input) is { } companion)
                return (await CopyFileAsync(companion, workDirectory, ct), companion);
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            reason ??= ex.Message;
        }
        if (reason is not null)
            logger.LogInformation("{File}: no motion video ({Reason})", input, reason);
        throw new ConversionRefusedException("error.no_motion_photo");
    }

    private static bool Unreadable(Exception ex) => ex is InvalidDataException or XmlException or EndOfStreamException;

    /// <summary>
    /// The photo of a LIVP, for the image engines: its bytes and format (<c>jpg</c> or <c>heic</c>).
    /// </summary>
    /// <exception cref="InvalidDataException">The package has no single photo, or it is too large.</exception>
    public static (byte[] Data, string Format) ReadStill(string livp)
    {
        using var zip = ZipFile.OpenRead(livp);
        var entry = Single(zip, StillExtensions, "photo");
        if (entry.Length <= 0 || entry.Length > MaxImageBytes)
            throw new InvalidDataException("The Live Photo's picture is empty or too large.");
        var data = new byte[entry.Length];
        using (var stream = entry.Open())
            stream.ReadExactly(data);
        var format = Path.GetExtension(entry.Name).ToLowerInvariant() is ".heic" or ".heif" ? "heic" : "jpg";
        return (data, format);
    }

    private static async Task<string?> CopyEmbeddedAsync(string input, string workDirectory, CancellationToken ct)
    {
        await using var file = File.OpenRead(input);
        if (MotionPhoto.FindVideo(file) is not { } video)
            return null;
        if (video.Length > MaxVideoBytes)
            throw new InvalidDataException("Motion Photo video exceeds 512 MiB.");
        var output = Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + ".mp4");
        await using var target = File.Create(output);
        file.Position = video.Offset;
        await MovieBoxes.CopyRangeAsync(file, target, video.Length, ct);
        return output;
    }

    /// <summary>
    /// The MOV saved next to an iPhone photo (IMG_0001.HEIC + IMG_0001.MOV), when both carry the same content
    /// identifier: a same-named video alone is not enough.
    /// </summary>
    private static string? PairedMovie(string input)
    {
        string? id;
        try
        {
            id = AppleLivePhoto.ImageIdentifier(input);
        }
        catch (MagickException)
        {
            return null;
        }
        if (id is null)
            return null;
        var full = Path.GetFullPath(input);
        var name = Path.GetFileNameWithoutExtension(full);
        var movies = Directory.EnumerateFiles(Path.GetDirectoryName(full)!)
            .Where(p => Path.GetExtension(p).Equals(".mov", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return movies.Count == 1 && id.Equals(AppleLivePhoto.MovieIdentifier(movies[0]), StringComparison.OrdinalIgnoreCase)
            ? movies[0]
            : null;
    }

    /// <summary>
    /// The MOV of a LIVP. The package makes the pair: a content identifier that doesn't match the photo's (or one that
    /// can't be read) is only logged.
    /// </summary>
    private static async Task<string> CopyPackageVideoAsync(string path, string workDirectory, ILogger logger, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var movie = Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + ".mov");
        await CopyEntryAsync(Single(zip, MovieExtensions, "video"), movie, MaxVideoBytes, ct);

        var stills = Entries(zip, StillExtensions);
        string? imageId = null;
        if (stills.Count == 1 && stills[0].Length is > 0 and <= MaxImageBytes)
        {
            var image = Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(stills[0].Name).ToLowerInvariant());
            await CopyEntryAsync(stills[0], image, MaxImageBytes, ct);
            try
            {
                imageId = AppleLivePhoto.ImageIdentifier(image);
            }
            catch (MagickException) { }
            File.Delete(image);
        }
        string? movieId = null;
        try
        {
            movieId = AppleLivePhoto.MovieIdentifier(movie);
        }
        catch (InvalidDataException) { }
        if (imageId is null || movieId is null || !imageId.Equals(movieId, StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("{File}: the photo's and video's Live Photo identifiers don't match ({Image} / {Movie}); taking the package's video",
                path, imageId ?? "none", movieId ?? "none");
        return movie;
    }

    /// <summary>Files of the package with one of <paramref name="extensions"/>, without macOS resource forks.</summary>
    private static List<ZipArchiveEntry> Entries(ZipArchive zip, string[] extensions)
    {
        if (zip.Entries.Count > MaxPackageEntries)
            throw new InvalidDataException("A LIVP holds a photo and its video, not this many files.");
        return zip.Entries
            .Where(e => e.Name.Length > 0 && !e.Name.StartsWith("._", StringComparison.Ordinal)
                        && !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                        && extensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))
            .ToList();
    }

    private static ZipArchiveEntry Single(ZipArchive zip, string[] extensions, string what)
    {
        var entries = Entries(zip, extensions);
        return entries.Count == 1 ? entries[0] : throw new InvalidDataException($"A LIVP must contain exactly one {what}.");
    }

    private static async Task CopyEntryAsync(ZipArchiveEntry entry, string target, long limit, CancellationToken ct)
    {
        if (entry.Length <= 0 || entry.Length > limit)
            throw new InvalidDataException("Live Photo archive entry is empty or too large.");
        // The target is a name of Filee's own in the work directory, whatever the entry is called.
        await using var source = entry.Open();
        await using var output = File.Create(target);
        await MovieBoxes.CopyRangeAsync(source, output, entry.Length, ct);
    }

    /// <summary>Copies the iPhone MOV, so the conversion reads a copy and never the user's file it might be told to replace.</summary>
    private static async Task<string> CopyFileAsync(string path, string workDirectory, CancellationToken ct)
    {
        await using var source = File.OpenRead(path);
        if (source.Length <= 0 || source.Length > MaxVideoBytes)
            throw new InvalidDataException("The Live Photo video is empty or too large.");
        var copy = Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + ".mov");
        await using var target = File.Create(copy);
        await MovieBoxes.CopyRangeAsync(source, target, source.Length, ct);
        return copy;
    }
}
