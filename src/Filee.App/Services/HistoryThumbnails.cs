// Small square pictures of converted files for the Home page's recent conversions. Skia decodes them off the UI
// thread, at a fraction of the picture's size where the format allows it (JPEG, WebP), from a copy of the file read in
// one go, so the file is held open only while it is read and others may still write, rename or delete it meanwhile.
// Video formats (MP4, MKV, WebM, etc.) have a representative frame extracted and scaled via FFmpeg off the UI thread
// without locking the file. A small cache keeps them while the files don't change: a search lists the same conversions
// again at once.

using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Engines.Media;
using SkiaSharp;

namespace Filee.App.Services;

internal static class HistoryThumbnails
{
    /// <summary>Width and height in pixels: the card shows it at 42, so this is sharp up to 200 % scaling.</summary>
    public const int Size = 96;

    /// <summary>Larger image files are not read: the card shows their format instead.</summary>
    internal const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Most pixels decoded for one thumbnail. JPEG and WebP decode at down to 1/8 of their size, so only a huge one
    /// comes near; PNG, GIF and BMP decode whole, so a larger one shows its format instead.
    /// </summary>
    internal const long MaxDecodedPixels = 4096L * 4096L;

    private const int CacheSize = 120;

    /// <summary>Pictures Skia reads on every platform.</summary>
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jfif", "webp", "gif", "bmp", "ico",
    };

    private sealed record Entry(DateTime Written, long Length, Bitmap? Bitmap);

    private static readonly Dictionary<string, Entry> Cache = new(FileSystemPaths.Comparer);
    private static readonly Lock CacheLock = new();

    /// <summary>Whether <paramref name="path"/> is a picture or video a thumbnail can be made of (by its extension only).</summary>
    public static bool CanShow(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return Readable.Contains(ext) || IsVideo(ext);
    }

    internal static bool IsVideo(string ext) =>
        FormatRegistry.FindByExtension(ext)?.Category == FormatCategory.Video;

    /// <summary>The thumbnail made last time for <paramref name="path"/>, without looking at the file.</summary>
    public static Bitmap? Cached(string path)
    {
        lock (CacheLock)
            return Cache.TryGetValue(path, out var entry) ? entry.Bitmap : null;
    }

    /// <summary>
    /// The thumbnail of <paramref name="path"/>, from the cache while the file is unchanged. Null when the file is gone,
    /// too large or not a picture/video Filee reads. Reads the disk: never call it on the UI thread.
    /// </summary>
    public static Bitmap? Load(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
                return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var written = file.LastWriteTimeUtc;
        var length = file.Length;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var hit) && hit.Written == written && hit.Length == length)
                return hit.Bitmap;
        }

        var ext = Path.GetExtension(path).TrimStart('.');
        var isVideo = IsVideo(ext);
        // A file that can't be read is remembered too, so a search doesn't try it again on every key.
        // Image files larger than MaxFileBytes are skipped to conserve memory; videos are seeked by FFmpeg directly.
        var bitmap = isVideo
            ? (length > 0 ? DecodeVideo(path, MaxDecodedPixels) : null)
            : (length <= MaxFileBytes ? Decode(path, MaxDecodedPixels) : null);

        lock (CacheLock)
        {
            if (Cache.Count >= CacheSize)
                Cache.Clear();
            Cache[path] = new Entry(written, length, bitmap);
        }
        return bitmap;
    }

    /// <summary>
    /// Extracts a representative frame of video <paramref name="path"/> using FFmpeg, scaled directly in the filtergraph,
    /// and decodes it into a <see cref="Size"/> square. Null when FFmpeg is unavailable, video is broken, or frame decoding fails.
    /// </summary>
    internal static Bitmap? DecodeVideo(string path, long maxPixels)
    {
        var ffmpeg = FfmpegConverter.Locate()?.Ffmpeg;
        if (ffmpeg is null || !File.Exists(ffmpeg))
            return null;

        var temp = Path.Combine(Path.GetTempPath(), $"filee-thumb-{Guid.NewGuid():N}.jpg");
        try
        {
            // One seek to 1s (or nearest keyframe) with FFmpeg scaling to min(Size, Size) to avoid huge 8K frame buffers.
            var arguments = new[]
            {
                "-hide_banner",
                "-nostdin",
                "-v", "error",
                "-ss", "1",
                "-i", Path.GetFullPath(path),
                "-frames:v", "1",
                "-vf", $"scale={Size}:{Size}:force_original_aspect_ratio=increase,crop={Size}:{Size}",
                "-y",
                temp
            };

            var result = FfmpegRunner.RunBriefly(ffmpeg, arguments, TimeSpan.FromSeconds(5));
            if (result.ExitCode != 0 || !File.Exists(temp))
            {
                // Trying once more without -ss when no file came out covers videos shorter than 1 s.
                var fallbackArguments = new[]
                {
                    "-hide_banner",
                    "-nostdin",
                    "-v", "error",
                    "-i", Path.GetFullPath(path),
                    "-frames:v", "1",
                    "-vf", $"scale={Size}:{Size}:force_original_aspect_ratio=increase,crop={Size}:{Size}",
                    "-y",
                    temp
                };
                result = FfmpegRunner.RunBriefly(ffmpeg, fallbackArguments, TimeSpan.FromSeconds(5));
                if (result.ExitCode != 0 || !File.Exists(temp))
                    return null;
            }

            using var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var data = SKData.Create(stream);
            if (data is null)
                return null;

            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                return null;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(info);
            if (codec.GetPixels(info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;

            return Square(decoded, codec.EncodedOrigin);
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Decodes <paramref name="path"/> into a <see cref="Size"/> square: upright (the EXIF orientation applied) and
    /// cropped from the middle. Null when it can't, or would have to decode more than <paramref name="maxPixels"/>.
    /// </summary>
    internal static Bitmap? Decode(string path, long maxPixels)
    {
        SKData? data;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            data = SKData.Create(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (data is null)
            return null;

        using (data)
        using (var codec = SKCodec.Create(data))
        {
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                return null;

            // The shorter side still covers the square; the codec picks the nearest scale it decodes at.
            var scale = Math.Min(1f, (float)Size / Math.Min(codec.Info.Width, codec.Info.Height));
            var scaled = codec.GetScaledDimensions(scale);
            if (scaled.Width <= 0 || scaled.Height <= 0 || (long)scaled.Width * scaled.Height > maxPixels)
                return null;

            var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(info);
            if (codec.GetPixels(info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;
            return Square(decoded, codec.EncodedOrigin);
        }
    }

    private static Bitmap Square(SKBitmap source, SKEncodedOrigin origin)
    {
        // Upright first: the orientations from LeftTop on turn the picture a quarter, swapping width and height.
        var turned = origin >= SKEncodedOrigin.LeftTop;
        using var upright = new SKBitmap(new SKImageInfo(turned ? source.Height : source.Width, turned ? source.Width : source.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(upright))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(Upright(origin, source.Width, source.Height));
            canvas.DrawBitmap(source, 0, 0);
        }

        var side = Math.Min(upright.Width, upright.Height);
        var crop = SKRect.Create((upright.Width - side) / 2f, (upright.Height - side) / 2f, side, side);
        using var square = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(square))
        using (var image = SKImage.FromBitmap(upright))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, crop, SKRect.Create(Size, Size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        // Avalonia copies the pixels.
        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, square.GetPixels(), new PixelSize(Size, Size),
            new Vector(96, 96), square.RowBytes);
    }

    /// <summary>
    /// Maps the stored pixels of a <paramref name="width"/> × <paramref name="height"/> picture to how it is shown, for
    /// each EXIF orientation (x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY).
    /// </summary>
    internal static SKMatrix Upright(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}
