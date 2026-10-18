using System.Xml.Linq;
using Filee.Core.Settings;
using Filee.Platform.Linux;

namespace Filee.Platform.Tests;

public sealed class LinuxDesktopIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Filee.Platform.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ContextMenuRoundTripPreservesOtherThunarActions()
    {
        var configuration = Path.Combine(_root, "config");
        var data = Path.Combine(_root, "data");
        var path = Path.Combine(configuration, "Thunar", "uca.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string existing = "<action><name>Keep me</name><unique-id>other</unique-id><command>echo unchanged</command><other-files /></action>";
        File.WriteAllText(path, "<actions>" + existing + "</actions>");
        var integration = new LinuxDesktopIntegration(configuration, data);

        integration.SetContextMenu(true, "/opt/Filee's app/Filee.App", "Filee로 변환 & test");
        integration.SetContextMenu(true, "/opt/new/Filee.App", "Updated");

        var document = XDocument.Load(path);
        Assert.Equal(2, document.Root!.Elements("action").Count());
        Assert.Equal("echo unchanged", document.Root.Elements("action")
            .Single(action => (string?)action.Element("unique-id") == "other").Element("command")!.Value);
        Assert.Equal("'/opt/new/Filee.App' --convert %F", document.Root.Elements("action")
            .Single(action => (string?)action.Element("unique-id") == LinuxDesktopIntegration.ThunarActionId).Element("command")!.Value);
        Assert.Contains("exec '/opt/new/Filee.App' --convert \"$@\"", File.ReadAllText(Path.Combine(data, "nautilus", "scripts", "Filee")));

        integration.SetContextMenu(false, "", "");

        document = XDocument.Load(path);
        Assert.Single(document.Root!.Elements("action"));
        Assert.Equal("other", document.Root.Element("action")!.Element("unique-id")!.Value);
        Assert.False(File.Exists(Path.Combine(data, "kio", "servicemenus", "filee.desktop")));
    }

    [Fact]
    public void MalformedThunarConfigurationIsNotOverwritten()
    {
        var configuration = Path.Combine(_root, "config");
        var path = Path.Combine(configuration, "Thunar", "uca.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<actions><action>");

        Assert.Throws<System.Xml.XmlException>(() =>
            new LinuxDesktopIntegration(configuration, Path.Combine(_root, "data"))
                .SetContextMenu(true, "/opt/Filee.App", "Convert"));
        Assert.Equal("<actions><action>", File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void AutostartEscapesLineBreaksAndKeepsTheExecutableAsOneArgument()
    {
        var integration = new LinuxDesktopIntegration(_root, Path.Combine(_root, "data"));
        integration.SetAutostart(true, "/opt/Filee test/Filee.App");
        var path = Path.Combine(_root, "autostart", "filee.desktop");
        Assert.Contains("Exec=\"/opt/Filee test/Filee.App\" --background\n", File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => integration.SetAutostart(true, "/opt/Filee\nExec=bad"));
        Assert.Contains("Exec=\"/opt/Filee test/Filee.App\" --background\n", File.ReadAllText(path));
        integration.SetAutostart(false, "");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DesktopArgumentsEscapeBothDesktopParsingLayers()
    {
        Assert.Equal("\"/tmp/a\\\\\"b\\\\$c\\\\`d\\\\\\\\e%%f\"",
            LinuxDesktopIntegration.DesktopArgument("/tmp/a\"b$c`d\\e%f"));
        Assert.Equal("'/tmp/a'\\''b $c'", LinuxDesktopIntegration.ShellArgument("/tmp/a'b $c"));
        Assert.Equal("label\\nExec=bad", LinuxDesktopIntegration.DesktopValue("label\nExec=bad"));
    }

    [Fact]
    public void ShortcutUpdatePreservesOthersAndIsIdempotent()
    {
        const string original = "; other comment\n(gtk_accel_path \"<Actions>/ThunarWindow/open\" \"<Control>o\")\n";
        var updated = LinuxDesktopIntegration.UpdateThunarShortcut(original, "<Control><Alt>space");
        Assert.StartsWith(original, updated);
        Assert.Contains("\"<Control><Alt>space\")\n", updated);
        Assert.Equal(updated, LinuxDesktopIntegration.UpdateThunarShortcut(updated, "<Control><Alt>space"));
        Assert.Equal(original, LinuxDesktopIntegration.UpdateThunarShortcut(updated, null));
        Assert.Throws<InvalidOperationException>(() => LinuxDesktopIntegration.UpdateThunarShortcut(original, "<Primary>o"));
        Assert.Throws<InvalidOperationException>(() => LinuxDesktopIntegration.UpdateThunarShortcut(
            original.Replace("<Control>o", "<Alt><Control>space", StringComparison.Ordinal), "<Control><Alt>space"));
        Assert.Throws<ArgumentException>(() => LinuxDesktopIntegration.UpdateThunarShortcut(original, "space\")\n(bad)"));
    }

    [Theory]
    [InlineData("Backslash", "backslash")]
    [InlineData("BackSlash", "backslash")]
    [InlineData("NumPad0", "KP_0")]
    [InlineData("NumPad7", "KP_7")]
    [InlineData("NumPad9", "KP_9")]
    [InlineData("PrintScreen", "Print")]
    [InlineData("Pause", "Pause")]
    [InlineData("PageDown", "Page_Down")]
    [InlineData("Space", "space")]
    public void RecordedKeysHaveGtkAccelerators(string recorded, string expected)
    {
        var gesture = new TriggerGesture
        {
            Kind = TriggerKind.KeyChord,
            Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt,
            Key = recorded,
        };
        var accelerator = LinuxDesktopIntegration.ToThunarAccelerator(gesture);
        Assert.Equal("<Control><Alt>" + expected, accelerator);
        var contents = LinuxDesktopIntegration.UpdateThunarShortcut("", accelerator);
        Assert.Contains("\"" + accelerator + "\"", contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
