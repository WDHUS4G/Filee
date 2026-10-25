// HAR (HTTP Archive, issue #328): the network requests a browser's developer tools record, saved as JSON. Read like
// JSON for the tree targets; as a table, one row per request with what a reader looks for first, so it reaches CSV,
// spreadsheets, Markdown, PDF and text through the table routes. Pages, timings and headers stay in the JSON.

namespace Filee.Engines.Data;

internal static class HarData
{
    private static readonly string[] Columns = ["Started", "Method", "URL", "Status", "Time (ms)", "Size (bytes)", "Type"];

    /// <summary>The requests of a HAR log (<c>log.entries</c>) as rows under a header row.</summary>
    /// <exception cref="InvalidDataException">The JSON has no <c>log.entries</c> list: it isn't a HAR file.</exception>
    public static List<List<string>> Table(object? tree)
    {
        if (Get(tree, "log") is not DataMap log || Get(log, "entries") is not List<object?> entries)
            throw new InvalidDataException("The file isn't a HAR log: it has no log.entries list.");
        List<List<string>> rows = [[.. Columns]];
        foreach (var entry in entries)
        {
            var request = Get(entry, "request");
            var response = Get(entry, "response");
            var content = Get(response, "content");
            // The body's size is in content.size; bodySize is -1 when the browser didn't know it.
            var size = Get(content, "size") ?? Get(response, "bodySize");
            rows.Add(
            [
                Text(Get(entry, "startedDateTime")),
                Text(Get(request, "method")),
                Text(Get(request, "url")),
                Text(Get(response, "status")),
                Text(Get(entry, "time")),
                size is long n && n < 0 ? "" : Text(size),
                Text(Get(content, "mimeType")),
            ]);
        }
        return rows;
    }

    private static object? Get(object? node, string key) => node is DataMap map && map.TryGetValue(key, out var value) ? value : null;

    private static string Text(object? value) => DataTree.IsScalar(value) ? DataTree.Text(value) : "";
}
