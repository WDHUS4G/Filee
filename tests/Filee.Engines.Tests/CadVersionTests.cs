// DWG and DXF in an older AutoCAD version (#489): every version reads back, and text survives the code page that
// versions before 2007 store it in.

using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Cad;

namespace Filee.Engines.Tests;

public class CadVersionTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Korean = "한글 도면 제목";
    private const string Chinese = "这是中文图纸"; // simplified: mostly not in CP949
    private const string Mixed = "평면도 这是 é";

    private async Task<string> ConvertAsync(string input, string target, CadVersion version)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target, Cad = { Version = version }, Output = { FileNamePattern = "{name}_out" } });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>The sample drawing, saved as AutoCAD 2018 with the ANSI_1252 code page newer drawings often keep.</summary>
    private string Source(string format)
    {
        var doc = CadBuilder.Sample();
        doc.Header.CodePage = "ANSI_1252";
        var layer = doc.Layers["Drawing"];
        CadBuilder.Add(doc, layer, new TextEntity(Korean) { InsertPoint = new XYZ(0, -10, 0), Height = 3 });
        CadBuilder.Add(doc, layer, new TextEntity(Chinese) { InsertPoint = new XYZ(0, -20, 0), Height = 3 });
        CadBuilder.Add(doc, layer, new MText(Mixed) { InsertPoint = new XYZ(0, -30, 0), Height = 3, RectangleWidth = 80 });
        var folder = fx.NewFolder();
        if (format == "dxf")
            return CadBuilder.SaveDxf(doc, folder, "source.dxf");
        var path = Path.Combine(folder, "source.dwg");
        DwgWriter.Write(path, doc);
        return path;
    }

    private static CadDocument ReadBack(string path) => CadFile.Read(path, Path.GetExtension(path)[1..]);

    /// <summary>The texts as shown: escapes decoded.</summary>
    private static string[] Shown(CadDocument doc) =>
    [
        .. doc.Entities.OfType<TextEntity>().Select(t => CadTextCodes.DecodeText(t.Value)),
        .. doc.Entities.OfType<MText>().Select(t => CadTextCodes.DecodeText(t.Value)),
    ];

    [Theory]
    [InlineData("dwg", CadVersion.AutoCad2018, ACadVersion.AC1032)]
    [InlineData("dwg", CadVersion.AutoCad2013, ACadVersion.AC1027)]
    [InlineData("dwg", CadVersion.AutoCad2010, ACadVersion.AC1024)]
    [InlineData("dwg", CadVersion.AutoCad2007, ACadVersion.AC1018)] // no 2007 DWG writer: 2004, which 2007 opens
    [InlineData("dwg", CadVersion.AutoCad2004, ACadVersion.AC1018)]
    [InlineData("dwg", CadVersion.AutoCad2000, ACadVersion.AC1015)]
    [InlineData("dxf", CadVersion.AutoCad2018, ACadVersion.AC1032)]
    [InlineData("dxf", CadVersion.AutoCad2013, ACadVersion.AC1027)]
    [InlineData("dxf", CadVersion.AutoCad2010, ACadVersion.AC1024)]
    [InlineData("dxf", CadVersion.AutoCad2007, ACadVersion.AC1021)]
    [InlineData("dxf", CadVersion.AutoCad2004, ACadVersion.AC1018)]
    [InlineData("dxf", CadVersion.AutoCad2000, ACadVersion.AC1015)]
    public async Task Each_version_reads_back_with_its_entities_and_text(string format, CadVersion choice, ACadVersion written)
    {
        var source = Source(format);
        var expected = CadBuilder.Census(ReadBack(source));

        var output = await ConvertAsync(source, format, choice);

        var back = ReadBack(output);
        Assert.Equal(written, back.Header.Version);
        Assert.Equal(expected, CadBuilder.Census(back));
        var shown = Shown(back);
        Assert.Contains(Korean, shown);
        Assert.Contains(Chinese, shown);
        Assert.Contains(Mixed, shown);
    }

    [Fact]
    public async Task An_old_dxf_gets_the_code_page_of_its_text_and_escapes_the_rest()
    {
        var output = await ConvertAsync(Source("dxf"), "dxf", CadVersion.AutoCad2004);

        var raw = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        // Korean is the script most of the text needs; AutoCAD names it ANSI_949.
        Assert.Matches(@"\$DWGCODEPAGE\r?\n\s*3\r?\nANSI_949\r?\n", raw);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Contains(Encoding.Latin1.GetString(Encoding.GetEncoding(949).GetBytes(Korean)), raw);
        Assert.Contains(@"\U+8FD9", raw); // 这 isn't in CP949
    }

    [Theory]
    [InlineData("dxf", CadVersion.AutoCad2000)]
    [InlineData("dxf", CadVersion.AutoCad2004)]
    [InlineData("dwg", CadVersion.AutoCad2000)]
    [InlineData("dwg", CadVersion.AutoCad2004)]
    public async Task Korean_names_pick_the_code_page_even_with_english_text(string format, CadVersion version)
    {
        // A layer and a block named in Korean, an attribute tag too, and only English text: common in Korean drawings.
        var doc = new CadDocument();
        doc.Header.CodePage = "ANSI_1252";
        var wall = new Layer("벽체") { Color = CadBuilder.Visible };
        doc.Layers.Add(wall);
        CadBuilder.Add(doc, wall, new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)));
        CadBuilder.Add(doc, wall, new TextEntity("Wall A") { InsertPoint = new XYZ(0, 5, 0), Height = 3 });
        CadBuilder.Add(doc, wall, new AttributeDefinition { Tag = "도면번호", Value = "A-101", Height = 3 });
        var source = format == "dxf" ? CadBuilder.SaveDxf(doc, fx.NewFolder(), "walls.dxf") : SaveDwg(doc, "walls.dwg");

        var back = ReadBack(await ConvertAsync(source, format, version));

        Assert.Contains(back.Layers, l => l.Name == "벽체");
        Assert.Contains(back.Entities.OfType<AttributeDefinition>(), a => a.Tag == "도면번호");
        Assert.Contains("Wall A", Shown(back));
    }

    [Fact]
    public async Task Names_and_text_the_code_page_cant_hold_are_escaped_and_an_emoji_is_one_question_mark()
    {
        var doc = new CadDocument();
        doc.Header.CodePage = "ANSI_1252";
        var layer = new Layer("평면 这") { Color = CadBuilder.Visible };
        doc.Layers.Add(layer);
        CadBuilder.Add(doc, layer, new TextEntity("한글 도면 😀 끝") { InsertPoint = new XYZ(0, 0, 0), Height = 3 });
        var source = CadBuilder.SaveDxf(doc, fx.NewFolder(), "mixed.dxf");

        var back = ReadBack(await ConvertAsync(source, "dxf", CadVersion.AutoCad2004));

        Assert.Contains(back.Layers, l => CadTextCodes.DecodeText(l.Name) == "평면 这");
        Assert.Contains("한글 도면 ? 끝", Shown(back));
    }

    private string SaveDwg(CadDocument doc, string name)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        DwgWriter.Write(path, doc);
        return path;
    }

    [Fact]
    public void The_code_page_follows_the_text()
    {
        Assert.Equal(949, CadCodePages.Choose([Korean], current: 1252));
        Assert.Equal(936, CadCodePages.Choose([Chinese], current: 1252));
        Assert.Equal(950, CadCodePages.Choose(["繁體中文說明"], current: 1252));
        Assert.Equal(932, CadCodePages.Choose(["ひらがなとカタカナ"], current: null));
        Assert.Equal(1251, CadCodePages.Choose(["Привет"], current: null));
        Assert.Equal(1252, CadCodePages.Choose(["Plan A"], current: null));
        Assert.Equal(950, CadCodePages.Choose(["Plan A"], current: 950)); // ASCII only: the drawing keeps its own
        Assert.Equal(949, CadCodePages.Number("ANSI_949"));
        Assert.Equal(936, CadCodePages.Number("gb2312"));
    }

    [Theory]
    [InlineData(CadVersion.SameAsSource, ACadVersion.AC1018, "dwg", ACadVersion.AC1018)]
    [InlineData(CadVersion.SameAsSource, ACadVersion.AC1021, "dwg", ACadVersion.AC1024)]
    [InlineData(CadVersion.SameAsSource, ACadVersion.AC1021, "dxf", ACadVersion.AC1021)]
    [InlineData(CadVersion.SameAsSource, ACadVersion.AC1014, "dxf", ACadVersion.AC1015)]
    [InlineData(CadVersion.SameAsSource, ACadVersion.AC1009, "dwg", ACadVersion.AC1015)]
    [InlineData(CadVersion.AutoCad2018, ACadVersion.AC1015, "dwg", ACadVersion.AC1032)]
    public void Same_as_the_source_stays_within_what_can_be_written(CadVersion choice, ACadVersion source, string format, ACadVersion written) =>
        Assert.Equal(written, CadFile.VersionFor(choice, source, format));

    [Fact]
    public async Task Objects_an_old_version_lacks_are_left_out_with_a_note()
    {
        var doc = CadBuilder.Sample();
        var lines = doc.Entities.OfType<Line>().Count();
        doc.Entities.Add(new MultiLeader());
        doc.Entities.Add(new Mesh());
        var source = CadBuilder.SaveDxf(doc, fx.NewFolder(), "new.dxf");

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "dwg", Cad = { Version = CadVersion.AutoCad2000 } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("notice.cad_newer_left_out", job.Files[0].NoticeKey);
        var back = DwgReader.Read(job.Outputs.Single());
        Assert.Empty(back.Entities.OfType<MultiLeader>());
        Assert.Empty(back.Entities.OfType<Mesh>());
        Assert.Equal(lines, back.Entities.OfType<Line>().Count());

        // In 2018 they stay, and there's no note.
        var current = await fx.ConvertAsync([source], new Preset { TargetFormat = "dxf", Output = { FileNamePattern = "{name}_2018" } });
        Assert.Null(current.Files[0].NoticeKey);
        Assert.Single(DxfReader.Read(current.Outputs.Single()).Entities.OfType<Mesh>());
    }

    [Fact]
    public async Task A_dwg_is_written_in_another_dwg_version_in_one_step()
    {
        var route = fx.Catalog.CreatePlanner().Plan("dwg", "dwg");
        Assert.Equal("cad", Assert.Single(route!.Steps).Converter.Id);

        var output = await ConvertAsync(Source("dwg"), "dwg", CadVersion.AutoCad2000);
        Assert.Equal("AC1015"u8.ToArray(), (await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken))[..6]);
    }
}
