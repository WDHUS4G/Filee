// What Setup carries: the published Filee (every file with its size and SHA-256), the version, and the conversion
// engines Setup can offer (from EngineDownloads, written at build time by build/tools/make-setup.cs).

using System.Text;
using System.Text.Json;

namespace Filee.Setup.Payload;

/// <summary>One file of the app, relative to the install folder (backslashes).</summary>
/// <param name="Segment">The LZMA stream it is in (files are in manifest order within it).</param>
/// <param name="Filter">"x86" when it went through the x86 branch filter before compressing.</param>
public sealed record PayloadFile(string Path, long Size, string Sha256, int Segment = 0, string Filter = "")
{
    public const string X86 = "x86";
}

/// <summary>An engine package of Settings → Engines, as Setup offers it on a fresh install.</summary>
/// <param name="MarkerFolders">Folders under %LocalAppData%\Filee\engines that are all marked when it is installed.</param>
public sealed record EngineOffer(string Id, IReadOnlyList<string> MarkerFolders, long DownloadSize, long InstalledSize, bool Suggested);

/// <summary>
/// Where the Setup program's own Authenticode signature (its certificate table) sits: the last bytes of the program,
/// in front of the payload. Setup.exe doesn't point to it (Setup.exe carries a signature of its own at the end); the
/// uninstaller copied out of Setup.exe points to it again, so it is signed too.
/// </summary>
public sealed record ProgramSignature(long Offset, int Size);

/// <param name="Segments">Compressed length of each LZMA stream, one after the other in the payload.</param>
/// <param name="LzmaProperties">The 5 bytes of LZMA properties every stream was written with (base64).</param>
/// <param name="Signature">The Setup program's signature, when it was signed before the payload was appended.</param>
public sealed record PayloadManifest(
    string Version,
    IReadOnlyList<PayloadFile> Files,
    string CompressedSha256,
    IReadOnlyList<EngineOffer> Engines,
    string EngineMarker,
    IReadOnlyList<long> Segments,
    string LzmaProperties,
    ProgramSignature? Signature = null)
{
    /// <summary>Format of the payload; a Setup refuses a payload with another one.</summary>
    public const int CurrentFormat = 1;

    public long TotalSize => Files.Sum(f => f.Size);

    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", CurrentFormat);
            writer.WriteString("version", Version);
            writer.WriteString("compression", "lzma");
            writer.WriteString("lzmaProperties", LzmaProperties);
            writer.WriteString("compressedSha256", CompressedSha256);
            writer.WriteString("engineMarker", EngineMarker);
            if (Signature is not null)
            {
                writer.WriteStartObject("programSignature");
                writer.WriteNumber("offset", Signature.Offset);
                writer.WriteNumber("size", Signature.Size);
                writer.WriteEndObject();
            }
            writer.WriteStartArray("segments");
            foreach (var segment in Segments)
                writer.WriteNumberValue(segment);
            writer.WriteEndArray();
            writer.WriteStartArray("engines");
            foreach (var engine in Engines)
            {
                writer.WriteStartObject();
                writer.WriteString("id", engine.Id);
                writer.WriteStartArray("markerFolders");
                foreach (var folder in engine.MarkerFolders)
                    writer.WriteStringValue(folder);
                writer.WriteEndArray();
                writer.WriteNumber("downloadSize", engine.DownloadSize);
                writer.WriteNumber("installedSize", engine.InstalledSize);
                writer.WriteBoolean("suggested", engine.Suggested);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("files");
            foreach (var file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("size", file.Size);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("segment", file.Segment);
                if (file.Filter.Length > 0)
                    writer.WriteString("filter", file.Filter);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Reads and checks a manifest; throws <see cref="InvalidDataException"/> when it isn't one Setup can use.</summary>
    public static PayloadManifest FromJson(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != CurrentFormat || root.GetProperty("compression").GetString() != "lzma")
                throw new InvalidDataException("Unknown payload format.");
            var segments = root.GetProperty("segments").EnumerateArray().Select(s => s.GetInt64()).ToList();
            var files = root.GetProperty("files").EnumerateArray()
                .Select(f => new PayloadFile(f.GetProperty("path").GetString()!, f.GetProperty("size").GetInt64(),
                    f.GetProperty("sha256").GetString()!, f.GetProperty("segment").GetInt32(),
                    f.TryGetProperty("filter", out var filter) ? filter.GetString() ?? "" : ""))
                .ToList();
            if (files.Any(f => f.Filter is not ("" or PayloadFile.X86)))
                throw new InvalidDataException("Unknown filter in the payload.");
            var properties = root.GetProperty("lzmaProperties").GetString()!;
            if (Convert.FromBase64String(properties).Length != 5)
                throw new InvalidDataException("Bad LZMA properties in the payload.");
            if (segments.Count == 0 || segments.Any(s => s <= 0)
                || files.Any(f => f.Segment < 0 || f.Segment >= segments.Count)
                || files.Zip(files.Skip(1)).Any(p => p.Second.Segment < p.First.Segment))
                throw new InvalidDataException("The payload's segments don't add up.");
            foreach (var file in files)
                CheckPath(file.Path);
            if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                throw new InvalidDataException("The payload lists a file twice.");
            var engines = root.TryGetProperty("engines", out var list)
                ? list.EnumerateArray().Select(e => new EngineOffer(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("markerFolders").EnumerateArray().Select(m => m.GetString()!).ToList(),
                    e.GetProperty("downloadSize").GetInt64(),
                    e.GetProperty("installedSize").GetInt64(),
                    e.GetProperty("suggested").GetBoolean())).ToList()
                : [];
            ProgramSignature? signature = null;
            if (root.TryGetProperty("programSignature", out var signed))
            {
                signature = new ProgramSignature(signed.GetProperty("offset").GetInt64(), signed.GetProperty("size").GetInt32());
                if (signature.Offset <= 0 || signature.Size <= 0)
                    throw new InvalidDataException("Bad program signature in the payload.");
            }
            return new PayloadManifest(
                root.GetProperty("version").GetString()!,
                files,
                root.GetProperty("compressedSha256").GetString()!,
                engines,
                root.TryGetProperty("engineMarker", out var marker) ? marker.GetString() ?? "" : "",
                segments,
                properties,
                signature);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException or FormatException)
        {
            throw new InvalidDataException("The payload manifest can't be read.", ex);
        }
    }

    /// <summary>
    /// A relative path that stays inside the install folder: no root, no "..", no drive, no alternate data stream.
    /// The payload is checked by its hash anyway; this keeps a broken one from writing elsewhere.
    /// </summary>
    public static void CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) || path.Contains(':')
            || path.Split('\\', '/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Bad path in the payload: {path}");
    }

    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    internal static Encoding Utf8 { get; } = new UTF8Encoding(false);
}
