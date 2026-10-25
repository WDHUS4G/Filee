using System.IO.Compression;
using System.Text;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public class DocumentFontsTests
{
    [Fact]
    public void Finds_fonts_used_by_ooxml_and_hwpx_but_not_embedded_or_theme_placeholders()
    {
        var path = Package(new Dictionary<string, string>
        {
            ["word/document.xml"] = "<root xmlns:w=\"urn:word\"><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Absent Serif\" w:eastAsia=\"+mj-ea\"/></root>",
            ["word/fontTable.xml"] = "<root><font name=\"Embedded Face\"><embedRegular/></font></root>",
            ["word/styles.xml"] = "<root><rFonts ascii=\"Embedded Face\"/></root>",
            ["xl/styles.xml"] = "<root><font><name val=\"Sheet Face\"/></font></root>",
            ["ppt/slides/slide1.xml"] = "<root><latin typeface=\"Slide Face\"/><ea typeface=\"\"/></root>",
            ["Contents/header.xml"] = "<root><font face=\"HWPX Face\"/></root>",
        });
        try
        {
            Assert.Equal(["Absent Serif", "Calibri", "HWPX Face", "Sheet Face", "Slide Face"], DocumentFonts.Referenced(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Installed_metric_compatible_families_suppress_the_warning()
    {
        var path = Package(new Dictionary<string, string>
        {
            ["word/document.xml"] = "<root><rFonts ascii=\"Calibri\" hAnsi=\"Times New Roman\" cs=\"Unknown Face\"/></root>",
        });
        try
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Carlito", "Liberation Serif" };
            Assert.Equal(["Unknown Face"], DocumentFonts.Missing(path, installed.Contains));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_non_package_never_prevents_conversion()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a package");
            Assert.Empty(DocumentFonts.Missing(path, _ => false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ignores_symbol_fonts_and_unrelated_large_office_parts()
    {
        var path = Package(new Dictionary<string, string>
        {
            ["word/numbering.xml"] = "<root><rFonts ascii=\"Symbol\" hAnsi=\"Wingdings\" eastAsia=\"Wingdings 2\" cs=\"Webdings\"/><rFonts ascii=\"Wingdings 3\" hAnsi=\"OpenSymbol\"/></root>",
            ["word/styles.xml"] = "<root><rFonts ascii=\"Real Word Face\"/></root>",
            ["word/comments.xml"] = "<root><rFonts ascii=\"Unrelated Word Face\"/></root>",
            ["xl/styles.xml"] = "<root><font><name val=\"Real Sheet Face\"/></font></root>",
            ["xl/worksheets/sheet1.xml"] = "<root><font><name val=\"Unrelated Sheet Face\"/></font></root>",
            ["ppt/slideLayouts/slideLayout1.xml"] = "<root><latin typeface=\"Real Slide Face\"/></root>",
            ["ppt/media/image1.xml"] = "<root><latin typeface=\"Unrelated Slide Face\"/></root>",
        });
        try
        {
            Assert.Equal(["Real Sheet Face", "Real Slide Face", "Real Word Face"], DocumentFonts.Missing(path, _ => false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Preflight_skips_legacy_formats_and_fails_open_on_any_metadata_error()
    {
        var legacy = Path.Combine(Path.GetTempPath(), $"filee-fonts-{Guid.NewGuid():N}.doc");
        var malformed = Package(new Dictionary<string, string> { ["word/styles.xml"] = "<root><rFonts" });
        var readable = Package(new Dictionary<string, string>
        {
            ["word/document.xml"] = "<root><rFonts ascii=\"Unavailable\"/></root>",
        });
        try
        {
            File.WriteAllText(legacy, "not a ZIP file");
            Assert.Empty(DocumentFonts.Missing(legacy, _ => throw new InvalidOperationException("should not inspect legacy files")));
            Assert.Empty(DocumentFonts.Missing(malformed, _ => false));
            Assert.Empty(DocumentFonts.Missing(readable, _ => throw new InvalidOperationException("font manager failure")));
            Assert.Throws<OperationCanceledException>(() => DocumentFonts.Missing(readable, _ => throw new OperationCanceledException()));
        }
        finally { File.Delete(legacy); File.Delete(malformed); File.Delete(readable); }
    }

    private static string Package(IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"filee-document-fonts-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(xml));
        }
        return path;
    }
}
