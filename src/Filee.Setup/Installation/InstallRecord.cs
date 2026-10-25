// uninstall.json next to Filee.exe: the version, who it was installed for and every file Setup put there. The
// uninstaller removes exactly these files (a folder chosen by the user may hold other things); an update replaces them
// and removes the ones the new version no longer has.

using System.Text.Json;

namespace Filee.Setup.Installation;

public sealed record InstallRecord(string Version, InstallScope Scope, IReadOnlyList<string> Files)
{
    public static string PathIn(string installFolder) => Path.Combine(installFolder, SetupNames.InstallRecordFile);

    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", Version);
            writer.WriteString("scope", Scope == InstallScope.Machine ? "machine" : "user");
            writer.WriteStartArray("files");
            foreach (var file in Files)
                writer.WriteStringValue(file);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>The record in <paramref name="installFolder"/>, or null when there is none or it can't be read.</summary>
    public static InstallRecord? Read(string installFolder)
    {
        try
        {
            var path = PathIn(installFolder);
            if (!File.Exists(path))
                return null;
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            var files = root.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToList();
            foreach (var file in files)
                Payload.PayloadManifest.CheckPath(file);
            return new InstallRecord(
                root.GetProperty("version").GetString() ?? "",
                root.TryGetProperty("scope", out var scope) && scope.GetString() == "user" ? InstallScope.User : InstallScope.Machine,
                files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }
}
