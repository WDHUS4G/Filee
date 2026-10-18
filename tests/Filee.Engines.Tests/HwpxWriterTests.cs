// HWPX from Markdown (Markdig), HTML (AngleSharp) and plain text, all built in, plus the writer's small helpers.
// Output is checked structurally, read back with Unhwp and rendered with rhwp. DOCX has its own tests
// (DocxToHwpxTests) because it is read without Pandoc.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;
using Unhwp;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class HwpxWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Markdown = """
        ---
        title: Filee 변환 테스트
        ---

        # 첫 번째 제목

        일반 문단에 **굵게**, *기울임*, ~~취소선~~, H~2~O, x^2^ 그리고 [링크](https://example.com) 가 있습니다.[^1]

        ## 두 번째 제목

        > 인용문도 사라지면 안 됩니다.

        3. 셋째부터 시작
        4. 다음 항목
           - 중첩 글머리
           - 두 번째 글머리

        a. 알파벳 목록
        b. 둘째

        | 이름 | 값 |
        |------|----|
        | 표 셀 | 42 |

        ![로고](logo.png)

        ```
        코드 블록
        ```

        [^1]: 각주 내용입니다.
        """;

    private async Task<string> ConvertAsync(string input, string target = "hwpx")
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        var errors = string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, errors);
        return job.Outputs.Single();
    }

    [Fact]
    public async Task Markdown_becomes_a_valid_hwpx_that_other_readers_understand()
    {
        // No Pandoc needed: Markdown is read with Markdig.
        var dir = fx.NewFolder();
        using (var logo = new MagickImage(MagickColors.MediumPurple, 240, 120))
            logo.Write(Path.Combine(dir, "logo.png"));
        var md = Path.Combine(dir, "문서.md");
        await File.WriteAllTextAsync(md, Markdown, TestContext.Current.CancellationToken);

        var hwpx = await ConvertAsync(md);

        ValidPackage(hwpx, expectImages: 1);
        var text = HwpxAssert.ReadBackText(hwpx);
        foreach (var expected in new[] { "첫 번째 제목", "굵게", "취소선", "인용문도 사라지면 안 됩니다", "셋째부터 시작", "중첩 글머리", "표 셀", "42", "코드 블록" })
            Assert.Contains(expected, text);

        // Unhwp's plain text skips footnotes, so check the footnote in the section XML.
        var footnote = Xml(hwpx).Descendants(Hp + "footNote").Single();
        Assert.Contains("각주 내용입니다", footnote.Value);
        RenderWithRhwp(fx, hwpx, "hwpx-from-markdown");
    }

    [Fact]
    public async Task Html_tables_with_merged_cells_keep_a_consistent_grid()
    {
        var dir = fx.NewFolder();
        var html = Path.Combine(dir, "table.html");
        // Row and column spans inside the body: every grid position must be covered exactly once.
        await File.WriteAllTextAsync(html, """
            <table>
              <tr><td rowspan="2">세로 병합</td><td colspan="2">가로 병합</td></tr>
              <tr><td>B</td><td>C</td></tr>
              <tr><td>D</td><td>E</td><td>F</td></tr>
            </table>
            """, TestContext.Current.CancellationToken);

        var hwpx = await ConvertAsync(html);

        ValidPackage(hwpx, expectImages: 0);
        var cells = Xml(hwpx).Descendants(Hp + "tc").Select(tc => (
            Row: (int)tc.Element(Hp + "cellAddr")!.Attribute("rowAddr")!, Col: (int)tc.Element(Hp + "cellAddr")!.Attribute("colAddr")!,
            RowSpan: (int)tc.Element(Hp + "cellSpan")!.Attribute("rowSpan")!, ColSpan: (int)tc.Element(Hp + "cellSpan")!.Attribute("colSpan")!,
            Height: (int)tc.Element(Hp + "cellSz")!.Attribute("height")!)).ToList();
        // Every grid position of the 3×3 table is covered exactly once.
        var covered = cells.SelectMany(c => Enumerable.Range(c.Row, c.RowSpan).SelectMany(r => Enumerable.Range(c.Col, c.ColSpan).Select(col => (r, col)))).ToList();
        Assert.Equal(9, covered.Count);
        Assert.Equal(9, covered.Distinct().Count());
        // Merged cells are as tall as the rows they span (as 한글 writes them).
        var rowHeight = (int row) => cells.First(c => c.Row == row && c.RowSpan == 1).Height;
        Assert.Equal(rowHeight(0) + rowHeight(1), cells.Single(c => c.RowSpan == 2).Height);
        RenderWithRhwp(fx, hwpx, "hwpx-merged-table");
    }

    [Fact]
    public async Task Plain_text_needs_no_pandoc()
    {
        var dir = fx.NewFolder();
        var txt = Path.Combine(dir, "메모.txt");
        await File.WriteAllTextAsync(txt, "첫 줄\n\n세 번째 줄 <태그> & 기호\u000B", TestContext.Current.CancellationToken);

        var hwpx = await ConvertAsync(txt);

        ValidPackage(hwpx, expectImages: 0);
        Assert.Contains("세 번째 줄 <태그> & 기호", HwpxAssert.ReadBackText(hwpx));
    }

    [Fact]
    public void Korean_text_files_in_the_legacy_code_page_are_decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string Text = "한글 텍스트 파일";
        Assert.Equal(Text, HwpxConverter.DecodeText(Encoding.GetEncoding(949).GetBytes(Text)));
        Assert.Equal(Text, HwpxConverter.DecodeText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)]));
        Assert.Equal(Text, HwpxConverter.DecodeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Text)).ToArray()));
    }

    [Fact]
    public void Docx_is_written_by_the_built_in_writer_in_one_step()
    {
        var catalog = new ConverterCatalog(fx.Converters) { Priority = new AppSettings().EnginePriority };
        var route = catalog.CreatePlanner().Plan("docx", "hwpx");
        Assert.Equal("hwpx-writer", Assert.Single(route!.Steps).Converter.Id);
    }

    [Theory]
    [InlineData("2in", 14400)]
    [InlineData("96px", 7200)]
    [InlineData("10mm", 2834)]
    [InlineData("1cm", 2834)]
    [InlineData("12pt", 1200)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void Lengths_are_converted_to_hwpunit(string? value, int? expected)
    {
        var actual = HwpxUnits.ParseLength(value);
        if (expected is null)
            Assert.Null(actual);
        else
            Assert.InRange(actual!.Value, expected.Value - 2, expected.Value + 2);
    }

    [Theory]
    [InlineData(0.1, "0.1 mm")]
    [InlineData(0.13, "0.12 mm")]
    [InlineData(0.353, "0.4 mm")]
    [InlineData(1.2, "1.0 mm")]
    [InlineData(9, "5.0 mm")]
    public void Line_widths_snap_to_the_widths_hangul_offers(double mm, string expected) =>
        Assert.Equal(expected, HwpxWriter.LineWidth(mm));

    [Fact]
    public void Text_is_escaped_and_invalid_xml_characters_removed() =>
        Assert.Equal("a &lt;b&gt; &amp; c", HwpxWriter.Escape("a <b> & c\u000B\u0001"));

    [Fact]
    public void Hangul_is_measured_wider_than_latin_text() =>
        Assert.True(HwpxWriter.TextWidth("가나다", 1000) > HwpxWriter.TextWidth("abc", 1000) * 1.5);
}
