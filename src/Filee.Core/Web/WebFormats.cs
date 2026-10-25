// What a file from the web is: by its media type (what a server or a drag says) and by its first bytes (what it
// really is). Only formats whose signature is unambiguous are sniffed; a ZIP is not taken for a DOCX, nor the reverse.

using System.Text;
using Filee.Core.Formats;

namespace Filee.Core.Web;

public static class WebFormats
{
    private static readonly Dictionary<string, string> ByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/apng"] = "apng",
        ["image/jpeg"] = "jpg",
        ["image/jpg"] = "jpg",
        ["image/pjpeg"] = "jpg",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
        ["image/bmp"] = "bmp",
        ["image/x-ms-bmp"] = "bmp",
        ["image/tiff"] = "tiff",
        ["image/svg+xml"] = "svg",
        ["image/avif"] = "avif",
        ["image/heic"] = "heic",
        ["image/heif"] = "heic",
        ["image/jxl"] = "jxl",
        ["image/x-icon"] = "ico",
        ["image/vnd.microsoft.icon"] = "ico",
        ["image/jp2"] = "jp2",
        ["image/vnd.adobe.photoshop"] = "psd",
        ["application/pdf"] = "pdf",
        ["application/x-pdf"] = "pdf",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/msword"] = "doc",
        ["application/vnd.ms-excel"] = "xls",
        ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/vnd.oasis.opendocument.text"] = "odt",
        ["application/vnd.oasis.opendocument.spreadsheet"] = "ods",
        ["application/vnd.oasis.opendocument.presentation"] = "odp",
        ["application/rtf"] = "rtf",
        ["text/rtf"] = "rtf",
        ["application/epub+zip"] = "epub",
        ["application/x-hwp"] = "hwp",
        ["application/haansofthwp"] = "hwp",
        ["application/vnd.hancom.hwp"] = "hwp",
        ["application/vnd.hancom.hwpx"] = "hwpx",
        ["application/hwp+zip"] = "hwpx",
        ["text/plain"] = "txt",
        ["text/csv"] = "csv",
        ["text/markdown"] = "md",
        ["application/json"] = "json",
        ["application/xml"] = "xml",
        ["text/xml"] = "xml",
        ["application/zip"] = "zip",
        ["application/x-zip-compressed"] = "zip",
        ["application/x-7z-compressed"] = "7z",
        ["application/vnd.rar"] = "rar",
        ["application/x-rar-compressed"] = "rar",
        ["application/gzip"] = "gz",
        ["audio/mpeg"] = "mp3",
        ["audio/mp4"] = "m4a",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/ogg"] = "ogg",
        ["audio/flac"] = "flac",
        ["audio/webm"] = "weba",
        ["video/mp4"] = "mp4",
        ["video/webm"] = "webm",
        ["video/quicktime"] = "mov",
        ["video/x-matroska"] = "mkv",
        ["video/x-msvideo"] = "avi",
        ["font/ttf"] = "ttf",
        ["font/otf"] = "otf",
        ["font/woff"] = "woff",
        ["font/woff2"] = "woff2",
    };

    /// <summary>Types a server sends for programs. Refused like the extensions in <see cref="WebFileNames.IsBlocked"/>.</summary>
    private static readonly HashSet<string> ProgramMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-msdownload", "application/x-msdos-program", "application/x-msi", "application/x-ms-installer",
        "application/vnd.microsoft.portable-executable", "application/x-dosexec", "application/x-executable",
        "application/x-sh", "application/x-shellscript", "application/x-bat", "application/java-archive",
        "application/x-java-archive", "application/vnd.android.package-archive", "application/x-apple-diskimage",
        "application/x-debian-package", "application/x-rpm", "application/x-mach-binary", "text/javascript",
        "application/javascript", "application/x-powershell",
    };

    /// <summary>The media type without parameters, lower case ("Image/PNG; charset=x" → "image/png"), or "".</summary>
    public static string Normalize(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return "";
        var semicolon = mime.IndexOf(';');
        return (semicolon >= 0 ? mime[..semicolon] : mime).Trim().ToLowerInvariant();
    }

    /// <summary>The extension (no dot) Filee writes for a media type it knows, or null.</summary>
    public static string? ExtensionForMime(string? mime) => ByMime.GetValueOrDefault(Normalize(mime));

    /// <summary>True for a web page (HTML) media type.</summary>
    public static bool IsPage(string? mime) => Normalize(mime) is "text/html" or "application/xhtml+xml";

    /// <summary>True for an image media type.</summary>
    public static bool IsImage(string? mime) => Normalize(mime).StartsWith("image/", StringComparison.Ordinal);

    /// <summary>True for a program's media type.</summary>
    public static bool IsProgram(string? mime) => ProgramMimes.Contains(Normalize(mime));

    /// <summary>Bytes enough for <see cref="Sniff"/>.</summary>
    public const int SniffLength = 64;

    /// <summary>
    /// The extension (no dot) of a format recognised by its first bytes, or null. Only formats whose signature can't be
    /// mistaken: images and PDF.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature))
            return "png";
        if (head.StartsWith(JpegSignature))
            return "jpg";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return "gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        if (head.StartsWith("%PDF-"u8))
            return "pdf";
        // BITMAPFILEHEADER: "BM", the file size, then two reserved words that are always zero.
        if (head.Length >= 14 && head.StartsWith("BM"u8) && head[6] == 0 && head[7] == 0 && head[8] == 0 && head[9] == 0)
            return "bmp";
        if (head.StartsWith(TiffLittle) || head.StartsWith(TiffBig))
            return "tiff";
        if (head.StartsWith(IcoSignature))
            return "ico";
        if (head.StartsWith("8BPS"u8))
            return "psd";
        if (head.StartsWith(JxlCodestream) || head.StartsWith(JxlContainer))
            return "jxl";
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            // The major brand, then the compatible brands: an AVIF may name itself "mif1" first.
            var brands = Encoding.ASCII.GetString(head[8..Math.Min(head.Length, 40)]);
            if (brands.Contains("avif", StringComparison.Ordinal) || brands.Contains("avis", StringComparison.Ordinal))
                return "avif";
            return brands[..4] is "heic" or "heix" or "heim" or "heis" or "mif1" or "msf1" or "hevc" or "hevx" ? "heic" : null;
        }
        return null;
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] TiffLittle = [0x49, 0x49, 0x2A, 0x00];
    private static readonly byte[] TiffBig = [0x4D, 0x4D, 0x00, 0x2A];
    private static readonly byte[] IcoSignature = [0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] JxlCodestream = [0xFF, 0x0A];
    private static readonly byte[] JxlContainer = [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private static readonly byte[][] ProgramSignatures =
    [
        [0x4D, 0x5A],                   // "MZ": Windows programs and libraries
        [0x7F, 0x45, 0x4C, 0x46],       // ELF: Linux programs
        [0x23, 0x21],                   // "#!": scripts
        [0xCA, 0xFE, 0xBA, 0xBE],       // Mach-O universal binaries (also Java classes)
        [0xCF, 0xFA, 0xED, 0xFE],       // Mach-O 64-bit
        [0xCE, 0xFA, 0xED, 0xFE],       // Mach-O 32-bit
    ];

    /// <summary>True when the bytes start like a program or a script (Windows, Linux or macOS executables, "#!").</summary>
    public static bool LooksLikeProgram(ReadOnlySpan<byte> head)
    {
        foreach (var signature in ProgramSignatures)
        {
            if (head.StartsWith(signature))
                return true;
        }
        return false;
    }

    /// <summary>True when the bytes start like an HTML page (a sign-in or error page instead of the file).</summary>
    public static bool LooksLikePage(ReadOnlySpan<byte> head)
    {
        var text = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name a file should have once its first bytes are known: a sniffed image or PDF replaces an extension that
    /// says otherwise (a ".jpg" URL that serves WebP), or is added to a name Filee can't place; a known extension
    /// whose format can't be sniffed (".docx" for a ZIP) stays.
    /// </summary>
    public static string NameFor(string name, ReadOnlySpan<byte> head, string? mime)
    {
        var sniffed = Sniff(head);
        var current = FormatRegistry.Detect(name);
        if (sniffed is not null)
        {
            var real = FormatRegistry.FindByExtension(sniffed);
            // "a.apng" sniffs as PNG and "a.ai" as PDF: keep a name whose format is a kind of what was sniffed.
            if (current is null || (current != real && IsSniffable(current)))
                return WebFileNames.WithExtension(name, sniffed);
            return name;
        }
        if (current is null && ExtensionForMime(mime) is { } fromMime)
            return WebFileNames.WithExtension(name, fromMime);
        return name;
    }

    private static readonly HashSet<string> SniffableIds = ["png", "jpg", "gif", "webp", "pdf", "bmp", "tiff", "ico", "psd", "jxl", "avif", "heic"];

    private static bool IsSniffable(FileFormat format) => SniffableIds.Contains(format.Id);
}
