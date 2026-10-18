// JSON source generation for types serialized by the app project itself.

using System.Text.Json.Serialization;

namespace Filee.App.Services;

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SupportersFile))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
