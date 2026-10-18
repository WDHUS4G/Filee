// KEPUB (Kobo) built in: EPUB → KEPUB adds Kobo's sentence spans to the spine's chapters and copies everything else,
// KEPUB → EPUB removes them, and other formats reach KEPUB through EPUB. Inputs come from EbookBuilders.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Ebooks;

namespace Filee.Engines.Tests;

public class KepubTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private static XDocument Chapter(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static List<XElement> Spans(XDocument document) =>
        document.Descendants(Xhtml + "span").Where(s => (string?)s.Attribute("class") == "koboSpan").ToList();

    [Fact]
    public void Kepub_is_detected_by_its_double_extension_and_reached_through_epub()
    {
        Assert.Equal("kepub", FormatRegistry.Detect("책.kepub.epub")!.Id);
        Assert.Equal("epub", FormatRegistry.Detect("책.epub")!.Id);
        Assert.Equal("kepub.epub", FormatRegistry.Get("kepub").PrimaryExtension);

        var planner = fx.Catalog.CreatePlanner();
        Assert.Equal("ebook", Assert.Single(planner.Plan("epub", "kepub")!.Steps).Converter.Id);
        Assert.Equal("ebook", Assert.Single(planner.Plan("kepub", "epub")!.Steps).Converter.Id);
        Assert.Equal(["ebook", "ebook"], planner.Plan("md", "kepub")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["ebook", "ebook"], planner.Plan("mobi", "kepub")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal("ebook", Assert.Single(planner.Plan("kepub", "txt")!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Epub_becomes_kepub_with_a_span_per_sentence_and_the_rest_unchanged()
    {
        var epub = EbookBuilders.Epub(fx.NewFolder());
        var kepub = await ConvertAsync(epub, "kepub");

        Assert.EndsWith(".kepub.epub", kepub);
        using var source = ZipFile.OpenRead(epub);
        using var zip = ZipFile.OpenRead(kepub);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength); // stored
        Assert.Equal(source.Entries.Select(e => e.FullName).Order(), zip.Entries.Select(e => e.FullName).Order());

        // Not in the spine: copied byte for byte.
        foreach (var name in new[] { "OEBPS/nav.xhtml", "OEBPS/content.opf", "OEBPS/Images/pic.png", "OEBPS/Styles/book.css" })
        {
            using var a = new MemoryStream();
            using var b = new MemoryStream();
            using (var s = source.GetEntry(name)!.Open()) s.CopyTo(a);
            using (var s = zip.GetEntry(name)!.Open()) s.CopyTo(b);
            Assert.Equal(a.ToArray(), b.ToArray());
        }

        var chapter = Chapter(zip, "OEBPS/Text/ch1.xhtml");
        var body = chapter.Root!.Element(Xhtml + "body")!;
        var columns = Assert.Single(body.Elements());
        Assert.Equal("book-columns", (string?)columns.Attribute("id"));
        Assert.Equal("book-inner", (string?)Assert.Single(columns.Elements()).Attribute("id"));

        var spans = Spans(chapter);
        Assert.Equal(spans.Count, spans.Select(s => (string?)s.Attribute("id")).Distinct().Count());
        Assert.Equal("kobo.1.1", (string?)spans[0].Attribute("id"));
        Assert.Equal("첫 번째 장", spans[0].Value);
        // The second paragraph: "밤하늘에 " "별" "이 " "반짝" "였다. " "크게" "외쳤다." (the space before it stays outside)
        // keep their inline elements.
        var second = spans.Where(s => ((string)s.Attribute("id")!).StartsWith("kobo.2.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["밤하늘에 ", "별", "이 ", "반짝", "였다. ", "크게", "외쳤다."], second.Select(s => s.Value));
        Assert.Equal("밤하늘에 별이 반짝였다. 크게 외쳤다.", second[0].Parent!.Value);
        Assert.Contains(second, s => s.Parent!.Name == Xhtml + "b" && s.Value == "별");
        // The text reads the same as before.
        var before = Chapter(source, "OEBPS/Text/ch1.xhtml").Root!.Element(Xhtml + "body")!.Value;
        Assert.Equal(before, body.Value);

        // Pictures get a span of their own.
        var pictures = Chapter(zip, "OEBPS/Text/ch2.xhtml").Descendants(Xhtml + "img").ToList();
        Assert.All(pictures, img => Assert.Equal("koboSpan", (string?)img.Parent!.Attribute("class")));
        // A file name with a space (escaped in the package document) is found.
        Assert.NotEmpty(Spans(Chapter(zip, "OEBPS/Text/ch 3.xhtml")));
        Assert.Empty(Spans(Chapter(zip, "OEBPS/nav.xhtml")));
    }

    [Fact]
    public async Task Kepub_reads_back_and_turns_into_a_plain_epub()
    {
        var kepub = await ConvertAsync(EbookBuilders.Epub(fx.NewFolder()), "kepub");

        var text = await File.ReadAllTextAsync(await ConvertAsync(kepub, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("밤하늘에 별이 반짝였다.", text);

        var epub = await ConvertAsync(kepub, "epub");
        Assert.Equal("epub", FormatRegistry.Detect(epub)!.Id);
        using var zip = ZipFile.OpenRead(epub);
        foreach (var name in new[] { "OEBPS/Text/ch1.xhtml", "OEBPS/Text/ch2.xhtml", "OEBPS/Text/ch 3.xhtml" })
        {
            var chapter = Chapter(zip, name);
            Assert.Empty(Spans(chapter));
            Assert.DoesNotContain(chapter.Descendants(), e => (string?)e.Attribute("id") is "book-columns" or "book-inner");
        }
        Assert.Contains("<b>별</b>", new StreamReader(zip.GetEntry("OEBPS/Text/ch1.xhtml")!.Open()).ReadToEnd());
    }

    [Fact]
    public async Task Markdown_becomes_kepub_through_epub()
    {
        var md = Path.Combine(fx.NewFolder(), "notes.md");
        await File.WriteAllTextAsync(md, "# Notes\n\nFirst sentence. Second sentence!\n\nAnother paragraph.\n", TestContext.Current.CancellationToken);

        var kepub = await ConvertAsync(md, "kepub");

        Assert.Equal("notes.kepub.epub", Path.GetFileName(kepub));
        using var zip = ZipFile.OpenRead(kepub);
        var spans = Kepub.ContentDocuments(zip).SelectMany(name => Spans(Chapter(zip, name))).Select(s => s.Value).ToList();
        Assert.Contains("First sentence. ", spans);
        Assert.Contains("Second sentence!", spans);
    }

    [Fact]
    public void Html_entities_and_a_second_pass_are_handled()
    {
        var document = Kepub.Load("<?xml version=\"1.0\"?><!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\"><html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>A&nbsp;b &amp; c&hellip; Done.</p><svg xmlns=\"http://www.w3.org/2000/svg\"><text>Skip me.</text></svg></body></html>"u8.ToArray());

        Assert.NotNull(document);
        Assert.NotNull(document.DocumentType);
        Assert.True(Kepub.AddSpans(document));
        Assert.Equal(["A b & c… ", "Done."], Spans(document).Select(s => s.Value));
        Assert.False(Kepub.AddSpans(document)); // already a KEPUB
        Assert.True(Kepub.RemoveSpans(document));
        Assert.Empty(Spans(document));
        Assert.Null(Kepub.Load("<html><p>not XML"u8.ToArray()));
    }

    [Theory]
    [InlineData("One. Two? Three!", new[] { "One. ", "Two? ", "Three!" })]
    [InlineData("Pi is 3.14 today.", new[] { "Pi is 3.14 today." })]
    [InlineData("He said \"Stop.\" Then left.", new[] { "He said \"Stop.\" ", "Then left." })]
    [InlineData("你好。再见！", new[] { "你好。", "再见！" })]
    [InlineData("별이 반짝였다. 크게 외쳤다.", new[] { "별이 반짝였다. ", "크게 외쳤다." })]
    public void Sentences_end_at_punctuation(string text, string[] expected) =>
        Assert.Equal(expected, Kepub.Sentences(text));
}
