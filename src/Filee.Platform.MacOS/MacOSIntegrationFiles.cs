using System.Xml.Linq;

namespace Filee.Platform.MacOS;

internal static class MacOSIntegrationFiles
{
    internal static string LaunchAgent(string executablePath) => Plist(Dict(
        ("Label", "com.filee.app"),
        ("AssociatedBundleIdentifiers", Array("com.filee.app")),
        ("ProgramArguments", Array(executablePath, "--background")),
        ("LimitLoadToSessionType", "Aqua"),
        ("RunAtLoad", true),
        ("KeepAlive", false)));

    /// <summary>
    /// The service's entry in the pbs NSServicesStatus preferences: "bundle id - menu title - message", quoted for
    /// defaults, which reads the key as a property list string.
    /// </summary>
    internal static string ServiceStatusKey =>
        $"\"com.filee.app - {MacOSServicesProvider.MenuItem} - {MacOSServicesProvider.Message}\"";

    /// <summary>On or off in the right-click menu and the Services menu, as System Settings writes it.</summary>
    internal static string ServiceStatus(bool enabled)
    {
        var on = enabled ? 1 : 0;
        return $"{{ \"enabled_context_menu\" = {on}; \"enabled_services_menu\" = {on}; " +
               $"\"presentation_modes\" = {{ \"ContextMenu\" = {on}; \"ServicesMenu\" = {on}; }}; }}";
    }

    private static string Plist(XElement dictionary) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));

    private static XElement Dict(params (string Key, object Value)[] entries) => new("dict",
        entries.SelectMany(entry => new[] { new XElement("key", entry.Key), Value(entry.Value) }));

    private static XElement Array(params object[] values) => new("array", values.Select(Value));

    private static XElement Value(object value) => value switch
    {
        XElement element => element,
        bool flag => new XElement(flag ? "true" : "false"),
        int number => new XElement("integer", number),
        string text => new XElement("string", text),
        _ => throw new ArgumentException("Unsupported property-list value.", nameof(value)),
    };
}
