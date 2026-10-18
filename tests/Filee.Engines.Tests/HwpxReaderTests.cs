// HWPX → document model (HwpxReader), the inverse of HwpxWriter: documents from the other readers (DOCX, Markdown,
// XLSX, PPTX) are written as HWPX and read back, then written and read again, which must give the same model.
// Files converted by rhwp, the template saved by 한글 and a package in the style 한글 writes (other prefixes,
// hp:switch branches, bullets, automatic page numbers, groups, tracked changes ...) are read too.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Office.Sheets;
using ImageMagick;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class HwpxReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// Writes <paramref name="source"/>, reads it back and compares; writes the result again and checks that reading
    /// it gives the same model and that the second file matches the first. Table row heights are layout caches the
    /// writer estimates from the model: where the source leaves spacing to the style, the model read back states it,
    /// so the second estimate may only be larger.
    /// </summary>
    private (HDocument Read, string Hwpx) RoundTrip(HDocument source, string name)
    {
        var dir = fx.NewFolder();
        var first = Path.Combine(dir, name + ".hwpx");
        HwpxWriter.Write(source, first);
        var skipped = new Dictionary<string, int>();
        var read = HwpxReader.Read(first, Path.Combine(dir, "media-1"), skipped);
        Assert.Empty(skipped); // everything Filee writes has a place in the model
        HDocumentAssert.Similar(source, read);

        var second = Path.Combine(dir, name + "-again.hwpx");
        HwpxWriter.Write(read, second);
        var again = HwpxReader.Read(second, Path.Combine(dir, "media-2"));
        Assert.Equal(HDocumentAssert.Dump(read, layout: false), HDocumentAssert.Dump(again, layout: false));
        var heights = (HDocument d) => d.Sections.SelectMany(s => s.Blocks).OfType<HTable>().SelectMany(t => t.Rows).Select(r => r.Height ?? 0).ToList();
        Assert.All(heights(read).Zip(heights(again)), h => Assert.True(h.Second >= h.First));
        Assert.Equal(Outline(first), Outline(second));
        return (read, first);
    }

    /// <summary>
    /// The section parts as a sequence of elements and text, without run boundaries and the ids that point into
    /// header.xml (formats are compared on the model), and without table heights (layout caches).
    /// </summary>
    private static string Outline(string hwpx)
    {
        using var zip = ZipFile.OpenRead(hwpx);
        var outline = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal)).OrderBy(e => e.FullName))
        {
            var root = XDocument.Load(entry.Open(), LoadOptions.PreserveWhitespace).Root!;
            foreach (var node in root.DescendantNodesAndSelf())
            {
                if (node is XText text)
                {
                    outline.Append(text.Value);
                    continue;
                }
                if (node is not XElement element || element.Name.LocalName is "run" or "t")
                    continue;
                var tableHeight = element.Name.LocalName == "cellSz" || (element.Name.LocalName == "sz" && element.Parent?.Name.LocalName == "tbl");
                var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration && !a.Name.LocalName.EndsWith("IDRef", StringComparison.Ordinal) &&
                                                                 !(tableHeight && a.Name.LocalName == "height"));
                outline.Append('<').Append(element.Name.LocalName).Append(string.Concat(attributes.Select(a => $" {a.Name.LocalName}={a.Value}"))).Append('>');
            }
        }
        return outline.ToString();
    }

    private static byte[] Png(MagickColor color, uint width = 120, uint height = 60)
    {
        using var image = new MagickImage(color, width, height);
        return image.ToByteArray(MagickFormat.Png);
    }

    private static IEnumerable<HInline> AllInlines(IEnumerable<HBlock> blocks) => blocks.SelectMany(b => b switch
    {
        HParagraph p => p.Inlines.SelectMany(i => i switch
        {
            HLink l => l.Content.Prepend(l),
            HNote n => AllInlines(n.Blocks).Prepend(n),
            HTextBox t => AllInlines(t.Blocks).Prepend(t),
            _ => [i],
        }),
        HTable t => AllInlines(t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).Concat(t.Caption)),
        _ => [],
    });

    private static string Text(IEnumerable<HBlock> blocks) => string.Concat(AllInlines(blocks).OfType<HText>().Select(t => t.Text));

    // ───────────────────────── Round trips ─────────────────────────

    /// <summary>
    /// Two sections (portrait with odd/even headers and a hidden first header, then landscape with two columns),
    /// formatting, lists, tab stops, line spacing kinds, a table with spans, borders, shading and a nested table,
    /// pictures inline and floating, a text box, shapes, footnote, endnote, links, bookmark and breaks.
    /// </summary>
    private string RichDocx(string folder)
    {
        var docx = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="1">
                  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="400" w:hanging="400"/></w:pPr></w:lvl>
                  <w:lvl w:ilvl="1"><w:start w:val="3"/><w:numFmt w:val="ganada"/><w:lvlText w:val="%2)"/><w:pPr><w:ind w:left="800" w:hanging="400"/></w:pPr></w:lvl>
                </w:abstractNum>
                <w:abstractNum w:abstractNumId="2"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/></w:lvl></w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                <w:num w:numId="2"><w:abstractNumId w:val="2"/></w:num>
                """,
            Settings = "<w:evenAndOddHeaders/>",
        };
        var picture = docx.Image(Png(MagickColors.MediumPurple));
        var header = docx.Header(P(R("홀수 쪽 머리말"), "<w:jc w:val=\"right\"/>"));
        var evenHeader = docx.Header(P(R("짝수 쪽 머리말")));
        var footer = docx.Footer(P(R("- ") + Field("PAGE", "1") + R(" / ") + Field("NUMPAGES", "3") + R(" -"), "<w:jc w:val=\"center\"/>"));
        docx.Footnotes("<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r>" + R(" 각주 설명") + "</w:p></w:footnote>");
        docx.Endnotes("<w:endnote w:id=\"1\"><w:p><w:r><w:endnoteRef/></w:r>" + R(" 미주 설명") + "</w:p></w:endnote>");
        var url = docx.Hyperlink("https://example.com/a?b=1");
        var cell = (string content, string tcPr) => $"<w:tc><w:tcPr>{tcPr}</w:tcPr>{content}</w:tc>";
        const string Column = "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>";
        const string Paragraph = "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>";
        string Shape(string geometry, string fill, string line, long cx, long cy, string text = "") =>
            $"<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"{geometry}\"/>{fill}{line}</wps:spPr>" +
            (text.Length > 0 ? $"<wps:txbx><w:txbxContent>{P(R(text))}</w:txbxContent></wps:txbx>" : "") + "<wps:bodyPr anchor=\"ctr\"/></wps:wsp>";
        var nested = "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/></w:tblGrid>" +
                     "<w:tr>" + cell(P(R("안1")), "") + cell(P(R("안2")), "") + "</w:tr></w:tbl>" + P("");

        docx.Paragraph(P("<w:bookmarkStart w:id=\"0\" w:name=\"intro\"/>" + R("가운데 제목") + "<w:bookmarkEnd w:id=\"0\"/>", "<w:pStyle w:val=\"Heading1\"/><w:jc w:val=\"center\"/>"))
            .Paragraph(P(R("굵은 빨강", "<w:b/><w:color w:val=\"FF0000\"/><w:sz w:val=\"28\"/><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"굴림\"/>") +
                         R(" 형광펜", "<w:highlight w:val=\"yellow\"/>") + R(" 기울임", "<w:i/><w:u w:val=\"single\"/><w:strike/>") +
                         R("위첨자", "<w:vertAlign w:val=\"superscript\"/>") + R("아래첨자", "<w:vertAlign w:val=\"subscript\"/>") +
                         R("자간", "<w:spacing w:val=\"20\"/>") + "<w:r><w:tab/></w:r>" + R("탭 뒤"),
                "<w:ind w:left=\"720\" w:hanging=\"360\"/><w:spacing w:before=\"240\" w:after=\"120\"/><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"8000\"/></w:tabs><w:keepNext/><w:keepLines/>"))
            .Paragraph(P(R("고정 줄 간격"), "<w:spacing w:line=\"360\" w:lineRule=\"exact\"/><w:ind w:right=\"400\" w:firstLine=\"300\"/><w:jc w:val=\"both\"/>"))
            .Paragraph(P(R("최소 줄 간격"), "<w:spacing w:line=\"300\" w:lineRule=\"atLeast\"/><w:jc w:val=\"distribute\"/>"))
            .Paragraph(P(R("첫 항목"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("하위 항목"), "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("글머리 항목"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr>"))
            .Paragraph(P(R("본문") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>" + R("미주") + "<w:r><w:endnoteReference w:id=\"1\"/></w:r>" +
                         $"<w:hyperlink r:id=\"{url}\">" + R("웹 링크", "<w:rStyle w:val=\"Hyperlink\"/>") + "</w:hyperlink>" +
                         "<w:hyperlink w:anchor=\"intro\">" + R("소개로 이동") + "</w:hyperlink>" + R("줄") + "<w:r><w:br/></w:r>" + R("바꿈")))
            .Paragraph("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:jc w:val=\"center\"/></w:tblPr>" +
                "<w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
                "<w:tr><w:trPr><w:tblHeader/></w:trPr>" + cell(P(R("가로 병합")), "<w:gridSpan w:val=\"2\"/><w:shd w:val=\"pct10\" w:color=\"auto\" w:fill=\"auto\"/>") +
                    cell(P(R("세로 병합")), "<w:vMerge w:val=\"restart\"/><w:vAlign w:val=\"center\"/>") + "</w:tr>" +
                "<w:tr>" + cell(nested, "") + cell(P(R("B")), "<w:vAlign w:val=\"bottom\"/>") + cell(P(""), "<w:vMerge/>") + "</w:tr>" +
                "<w:tr>" + cell(P(R("C")), "<w:tcBorders><w:bottom w:val=\"double\" w:sz=\"12\" w:color=\"FF0000\"/></w:tcBorders>") +
                    cell(P(R("D")), "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FFF2CC\"/>") + cell(P(R("E")), "<w:tcBorders><w:left w:val=\"dashed\" w:sz=\"8\" w:color=\"0000FF\"/></w:tcBorders>") + "</w:tr>" +
                "</w:tbl>")
            .Paragraph(P(Anchor(PictureUri, Picture(picture, 914400, 457200), 914400, 457200,
                "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>", Paragraph) + R("그림 옆에 흐르는 글")))
            .Paragraph(P(InlinePicture(picture, 457200, 457200)))
            .Paragraph(P(Anchor(ShapeUri,
                Shape("roundRect", "<a:solidFill><a:srgbClr val=\"FFF2CC\"/></a:solidFill>", "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln>", 1828800, 914400, "글상자 안의 글"),
                1828800, 914400, "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>",
                "<wp:positionV relativeFrom=\"page\"><wp:posOffset>3600000</wp:posOffset></wp:positionV>", "<wp:wrapTopAndBottom/>")))
            .Paragraph(P(Anchor(ShapeUri, Shape("line", "", "<a:ln w=\"25400\"><a:solidFill><a:srgbClr val=\"4F81BD\"/></a:solidFill><a:prstDash val=\"dash\"/></a:ln>", 3000000, 0),
                3000000, 0, Column, Paragraph, "<wp:wrapNone/>")))
            .Paragraph(P(Anchor(ShapeUri,
                Shape("ellipse", "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"99CCFF\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"FFFFFF\"/></a:gs></a:gsLst><a:lin ang=\"5400000\"/></a:gradFill>", "<a:ln><a:noFill/></a:ln>", 1000000, 500000),
                1000000, 500000, Column, Paragraph, "<wp:wrapNone/>")))
            .Paragraph(P(R("앞 쪽") + "<w:r><w:br w:type=\"page\"/></w:r>" + R("다음 쪽")))
            .Paragraph(P(R("첫 구역 끝"),
                $"<w:sectPr><w:headerReference w:type=\"default\" r:id=\"{header}\"/><w:headerReference w:type=\"even\" r:id=\"{evenHeader}\"/><w:footerReference w:type=\"default\" r:id=\"{footer}\"/>" +
                $"{DefaultPage}<w:titlePg/><w:pgNumType w:start=\"3\"/></w:sectPr>"))
            .Paragraph(P(R("가로 두 단")))
            .Paragraph(P(R("다음 단")) + P("<w:r><w:br w:type=\"column\"/></w:r>" + R("둘째 단")));
        docx.SectionProperties =
            "<w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/>" +
            "<w:pgMar w:top=\"1985\" w:right=\"1134\" w:bottom=\"1418\" w:left=\"1134\" w:header=\"851\" w:footer=\"851\" w:gutter=\"0\"/><w:cols w:num=\"2\" w:space=\"720\" w:sep=\"1\"/>";
        return docx.Save(Path.Combine(folder, "rich.docx"));
    }

    [Fact]
    public void Docx_documents_survive_the_way_through_hwpx()
    {
        var dir = fx.NewFolder();
        var source = DocxReader.Read(RichDocx(dir), Path.Combine(dir, "docx-media"));

        var (read, _) = RoundTrip(source, "docx");

        // Spot checks on top of the structural comparison.
        Assert.Equal(2, read.Sections.Count);
        var first = read.Sections[0];
        Assert.Equal([HPageType.Odd, HPageType.Even], first.Headers.Select(h => h.Pages));
        Assert.Equal("홀수 쪽 머리말", Text(first.Headers[0].Blocks));
        Assert.True(first.HideFirstHeader);
        Assert.Equal(3, first.StartPageNumber);
        Assert.Equal([HFieldKind.PageNumber, HFieldKind.TotalPages], AllInlines(first.Footers.Single().Blocks).OfType<HField>().Select(f => f.Kind));
        var landscape = read.Sections[1];
        Assert.True(landscape.Page!.Landscape);
        Assert.Equal((16838 * 5, 11906 * 5), (landscape.Page.Width, landscape.Page.Height));
        Assert.Equal(new HColumns(2, 3600, true), landscape.Columns);

        // Hanging indent: the model measures "left" to the second line like Word.
        var indented = first.Blocks.OfType<HParagraph>().Single(p => Text([p]).StartsWith("굵은 빨강", StringComparison.Ordinal));
        Assert.Equal((3600, -1800), (indented.Format.Left, indented.Format.FirstLine));
        var red = AllInlines([indented]).OfType<HText>().First();
        Assert.Equal((true, 1400, "Arial", "굴림", "#FF0000"), (red.Format.Bold, red.Format.Size, red.Format.Font, red.Format.EastAsianFont, red.Format.Color));

        var notes = AllInlines(first.Blocks).OfType<HNote>().ToList();
        Assert.Equal([("각주 설명", false), ("미주 설명", true)], notes.Select(n => (Text(n.Blocks), n.Endnote)));
        var links = AllInlines(first.Blocks).OfType<HLink>().ToList();
        Assert.Equal(["https://example.com/a?b=1", "#intro"], links.Select(l => l.Target));

        var table = first.Blocks.OfType<HTable>().Single();
        Assert.True(table.Rows[0].Header);
        Assert.Equal((1, 2), (table.Rows[0].Cells[0].RowSpan, table.Rows[0].Cells[0].ColSpan));
        Assert.Equal((2, 1), (table.Rows[0].Cells[1].RowSpan, table.Rows[0].Cells[1].ColSpan));
        Assert.Equal("안1안2", Text([Assert.IsType<HTable>(table.Rows[1].Cells[0].Blocks[0])]));

        Assert.Equal(2, AllInlines(first.Blocks).OfType<HImage>().Count());
        Assert.Contains(AllInlines(first.Blocks), i => i is HTextBox { Fill: "#FFF2CC" } box && Text(box.Blocks) == "글상자 안의 글");
        Assert.Contains(AllInlines(first.Blocks), i => i is HShape { Kind: HShapeKind.Ellipse, Gradient.Colors.Count: 2 });
        Assert.Contains(first.Blocks, b => b is HParagraph { PageBreakBefore: true });
        Assert.Contains(landscape.Blocks, b => b is HParagraph { ColumnBreakBefore: true });
    }

    [Fact]
    public void Markdown_documents_survive_the_way_through_hwpx()
    {
        var dir = fx.NewFolder();
        using (var logo = new MagickImage(MagickColors.MediumPurple, 240, 120))
            logo.Write(Path.Combine(dir, "logo.png"));
        const string Markdown = """
            ---
            title: 마크다운 제목
            ---

            # 첫 번째 제목

            일반 문단에 **굵게**, *기울임*, ~~취소선~~, H~2~O, x^2^, `코드` 그리고 [링크](https://example.com) 가 있습니다.[^1]

            > 인용문

            3. 셋째부터 시작
            4. 다음 항목
               - 중첩 글머리

            | 이름 | 값 |
            |------|---:|
            | 표 셀 | 42 |

            ![로고](logo.png)

            ```
            코드 블록
            ```

            [^1]: 각주 내용입니다.
            """;
        var source = MarkdownReader.Read(Markdown, dir);

        var (read, _) = RoundTrip(source, "markdown");

        Assert.Equal("마크다운 제목", read.Title);
        var blocks = read.Sections.Single().Blocks;
        Assert.Equal(1, blocks.OfType<HParagraph>().First(p => p.Inlines.Count > 0).HeadingLevel);
        Assert.Contains("각주 내용입니다", Text(AllInlines(blocks).OfType<HNote>().Single().Blocks));
        Assert.Equal(3, blocks.OfType<HParagraph>().Single(p => Text([p]) == "셋째부터 시작").List!.Numbering.Levels[0].Start);
        Assert.Single(AllInlines(blocks).OfType<HImage>());
    }

    [Fact]
    public void Spreadsheets_survive_the_way_through_hwpx()
    {
        var xlsx = new XlsxBuilder();
        xlsx.Sheet("매출",
            XlsxBuilder.Row(1, xlsx.Text("A1", "지점별 매출", XlsxBuilder.Header)) +
            XlsxBuilder.Row(2, xlsx.Text("A2", "지점", XlsxBuilder.Header), xlsx.Text("B2", "매출", XlsxBuilder.Header)) +
            XlsxBuilder.Row(3, xlsx.Text("A3", "서울"), XlsxBuilder.Number("B3", 1350000, XlsxBuilder.Won)) +
            XlsxBuilder.Row(4, xlsx.Text("A4", "합계"), $"<c r=\"B4\" s=\"{XlsxBuilder.Total}\"><v>1350000</v></c>"),
            before: "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"2\" topLeftCell=\"A3\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>",
            after: "<mergeCells count=\"1\"><mergeCell ref=\"A1:B1\"/></mergeCells>");
        xlsx.Sheet("넓은 표", XlsxBuilder.Row(1, [.. Enumerable.Range(0, 12).Select(c => xlsx.Text($"{(char)('A' + c)}1", $"항목 {c + 1}"))]),
            before: "<cols><col min=\"1\" max=\"12\" width=\"12\" customWidth=\"1\"/></cols>");
        var source = SheetDocument.Build(XlsxReader.Read(xlsx.Save(Path.Combine(fx.NewFolder(), "매출.xlsx"))));

        var (read, _) = RoundTrip(source, "xlsx");

        var table = read.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal((1, 2), (table.Rows[0].Cells[0].RowSpan, table.Rows[0].Cells[0].ColSpan));
        Assert.Equal("#1F4E79", table.Rows[0].Cells[0].Fill);
        Assert.Equal([true, true, false, false], table.Rows.Select(r => r.Header));
        Assert.True(read.Sections[1].Page!.Landscape);
    }

    [Fact]
    public void Excel_page_breaks_start_a_new_page_with_the_header_rows_again()
    {
        // Issue #67: page breaks set in Excel were ignored by XLSX → PDF.
        var xlsx = new XlsxBuilder();
        xlsx.Sheet("목록",
            XlsxBuilder.Row(1, xlsx.Text("A1", "번호", XlsxBuilder.Header)) +
            string.Concat(Enumerable.Range(2, 8).Select(r => r == 7
                ? "<row r=\"7\" hidden=\"1\"><c r=\"A7\"><v>6</v></c></row>"
                : XlsxBuilder.Row(r, XlsxBuilder.Number($"A{r}", r - 1)))),
            before: "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>",
            after: "<rowBreaks count=\"2\" manualBreakCount=\"2\"><brk id=\"4\" max=\"16383\" man=\"1\"/><brk id=\"6\" max=\"16383\" man=\"1\"/></rowBreaks>");
        var book = XlsxReader.Read(xlsx.Save(Path.Combine(fx.NewFolder(), "목록.xlsx")));
        Assert.Equal([4, 6], book.Sheets[0].RowBreaks);

        var source = SheetDocument.Build(book);

        // Rows 2-4 | 5-6 | 8-9: the break above the hidden row 7 starts the page at row 8.
        var tables = source.Sections[0].Blocks.OfType<HTable>().ToList();
        Assert.Equal([false, true, true], tables.Select(t => t.PageBreakBefore));
        Assert.Equal([4, 3, 3], tables.Select(t => t.Rows.Count));
        Assert.All(tables, t => Assert.True(t.Rows[0].Header));
        var dir = fx.NewFolder();
        var hwpx = Path.Combine(dir, "목록.hwpx");
        HwpxWriter.Write(source, hwpx);
        using var zip = System.IO.Compression.ZipFile.OpenRead(hwpx);
        using var reader = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(reader.ReadToEnd(), "pageBreak=\"1\""));
    }

    [Fact]
    public void Presentations_survive_the_way_through_hwpx()
    {
        var pptx = new PptxBuilder();
        var chart = pptx.Image(Png(MagickColors.Teal, 160, 90));
        pptx.Slide(pptx.Title("2026년 사업 보고"));
        pptx.Slide(
            pptx.Title("다음 분기 계획") +
            pptx.Body(("매출 증가", 0), ("지점 확대", 1)) +
            pptx.Shape("roundRect", 838200, 4400000, 3000000, 1500000, "#7F77DD", "강조") +
            pptx.Picture(chart, 8000000, 1900000, 3000000, 1687500) +
            pptx.Table(4000000, 4400000, 1200000, 370840, ["지점", "매출"], ["강남", "16.2억"]) +
            pptx.Group(6000000, 4000000, 2000000, 1000000, 0.5, pptx.Shape("ellipse", 0, 0, 4000000, 2000000, "#1D9E75", "단계 1")),
            background: "#1F1E2D");
        var dir = fx.NewFolder();
        var source = PptxReader.Read(pptx.Save(Path.Combine(dir, "계획.pptx")), Path.Combine(dir, "pptx-media"));

        var (read, _) = RoundTrip(source, "pptx");

        var inlines = AllInlines(read.Sections.SelectMany(s => s.Blocks)).ToList();
        Assert.Contains(inlines, i => i is HTextBox { Shape: HShapeKind.Ellipse } box && Text(box.Blocks) == "단계 1");
        Assert.Single(inlines.OfType<HImage>(), i => i.Anchor is { HorizontalRelativeTo: "PAPER" });
        Assert.All(inlines.OfType<HTextBox>(), b => Assert.NotNull(b.Anchor));
    }

    [Fact]
    public void A_picture_background_covers_the_slide()
    {
        // Issue #49: a chalkboard picture set as the slide background (Format Background → Picture) was dropped,
        // leaving white titles on a white page.
        var pptx = new PptxBuilder();
        var chalkboard = pptx.Image(Png(MagickColors.DarkSlateGray, 160, 90));
        pptx.Slide(pptx.Title("채널A, 〈금쪽같은 내새끼〉"), backgroundImage: chalkboard);
        var dir = fx.NewFolder();

        var read = PptxReader.Read(pptx.Save(Path.Combine(dir, "칠판.pptx")), Path.Combine(dir, "pptx-media"));

        var background = Assert.Single(AllInlines(read.Sections.SelectMany(s => s.Blocks)).OfType<HImage>());
        Assert.Equal((96000, 54000), (background.Width, background.Height)); // the slide, in HWPUNIT
        Assert.Equal((0, 0), (background.Anchor!.HorizontalOffset, background.Anchor.VerticalOffset));
    }

    // ───────────────────────── Other producers ─────────────────────────

    [Fact]
    public async Task Files_converted_by_rhwp_are_read_like_the_original()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not available");
        var dir = fx.NewFolder();
        var hwpx = Path.Combine(dir, "original.hwpx");
        HwpxWriter.Write(DocxReader.Read(RichDocx(dir), Path.Combine(dir, "docx-media")), hwpx);

        // HWPX → HWP → HWPX: the HWPX rhwp writes for an HWP file, the way HWP documents reach the reader.
        async Task<string> Convert(string input, string from, string to)
        {
            var output = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(input)}-rhwp.{to}");
            var written = await rhwp.ConvertAsync(new ConversionStep(input, from, to, new Preset(), new FixedPath(output), dir), null, TestContext.Current.CancellationToken);
            return written.Single();
        }
        var fromHwp = await Convert(await Convert(hwpx, "hwpx", "hwp"), "hwp", "hwpx");

        var skipped = new Dictionary<string, int>();
        var original = HwpxReader.Read(hwpx, Path.Combine(dir, "media-original"));
        var converted = HwpxReader.Read(fromHwp, Path.Combine(dir, "media-rhwp"), skipped);

        Assert.Empty(skipped);
        HDocumentAssert.Similar(original, converted);
    }

    [Fact]
    public void The_template_saved_by_hangul_is_read()
    {
        // blank.hwpx (from pypandoc-hwpx, MIT) was saved by Hancom Office Hangul 12.30.
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "blank.hwpx");
        using (var template = HwpxWriter.OpenBuiltInTemplate())
        using (var file = File.Create(path))
            template.CopyTo(file);

        var document = HwpxReader.Read(path, Path.Combine(dir, "media"));

        var section = Assert.Single(document.Sections);
        Assert.Equal(new HPage(59530, 84190, 7200, 7200, 4255, 4960, 4250, 2240, 0), section.Page);
        Assert.Equal(HColumns.Single, section.Columns);
        var paragraph = Assert.IsType<HParagraph>(Assert.Single(section.Blocks));
        Assert.Empty(paragraph.Inlines);
        // Normal paragraph shape: 180% line spacing, 10 pt after (hp:case), not the doubled hp:default values.
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 180), paragraph.Format.LineSpacing);
        Assert.Equal(1000, paragraph.Format.After);
        Assert.Equal(1000, paragraph.MarkFormat.Size);
    }

    [Fact]
    public void Packages_in_the_style_of_hangul_are_read()
    {
        var path = HangulStylePackage(fx.NewFolder());
        var skipped = new Dictionary<string, int>();

        var document = HwpxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media"), skipped);

        Assert.Equal("한글 문서 제목", document.Title);
        Assert.Equal(2, document.Sections.Count);
        var section = document.Sections[0];
        Assert.Equal(new HPage(84188, 59528, 8504, 8504, 5668, 4252, 4252, 4252, 0), section.Page);
        Assert.Equal(new HColumns(2, 850, true), section.Columns);
        Assert.True(section.HideFirstHeader);
        Assert.Equal(5, section.StartPageNumber);
        Assert.Equal("홀수 쪽 머리말", Text(Assert.Single(section.Headers, h => h.Pages == HPageType.Odd).Blocks));
        // Automatic page number (hp:pageNum) at the bottom centre with dashes.
        var pageNumber = Assert.IsType<HParagraph>(Assert.Single(Assert.Single(section.Footers).Blocks));
        Assert.Equal(HAlign.Center, pageNumber.Format.Align);
        Assert.Equal(["T:- ", "PageNumber", "T: -"], pageNumber.Inlines.Select(i => i is HText t ? "T:" + t.Text : ((HField)i).Kind.ToString()));

        var blocks = section.Blocks;
        var heading = Assert.IsType<HParagraph>(blocks[0]);
        Assert.Equal((1, "제1장 개요"), (heading.HeadingLevel, Text([heading])));
        Assert.Equal(("^1.", 0), (heading.List!.Numbering.Levels[0].Text, heading.List.Level)); // outline numbering
        Assert.Equal(1000, heading.Format.Left); // margin without a switch: the doubled scale, halved
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 150), heading.Format.LineSpacing);

        var formatted = Assert.IsType<HParagraph>(blocks[1]);
        Assert.Equal(new HParaFormat(HAlign.Left, 3000, 500, -2000, 400, 600, new HLineSpacing(HLineSpacingKind.Fixed, 1800), true, true, true,
            [new HTabStop(20000, HTabKind.Center, "DASH")]), formatted.Format);
        var texts = formatted.Inlines.Select(i => i switch
        {
            HText t => t.Text,
            HTab => "<tab>",
            HLineBreak => "<br>",
            _ => "?",
        });
        Assert.Equal(["굵게", "보통", "형광", " 묶음 빈칸 고정", "<tab>", "탭", "<br>", "둘째 줄넣은 글하이­픈"], texts);
        var bold = ((HText)formatted.Inlines[0]).Format;
        Assert.Equal(new HCharFormat(true, true, true, true, true, false, 1400, "Times New Roman", "맑은 고딕", "#0000FF", "#FFFF00", -5), bold);
        Assert.Equal("#00FF00", ((HText)formatted.Inlines[2]).Format.Shade); // highlighter (markpen)
        Assert.Equal(new HCharFormat(false, false, false, false, false, false, 1000, "함초롬바탕", "함초롬바탕", "#000000", null, 0), ((HText)formatted.Inlines[1]).Format);

        var links = Assert.IsType<HParagraph>(blocks[2]).Inlines;
        Assert.Equal("처음", Assert.IsType<HBookmark>(links[0]).Name);
        var web = Assert.IsType<HLink>(links[1]);
        Assert.Equal(("http://www.hancom.com?a=1", "한컴링크"), (web.Target, Text([ParagraphOf(web.Content)])));
        Assert.Equal(" 뒤", Assert.IsType<HText>(links[2]).Text);
        Assert.Equal("#처음", Assert.IsType<HLink>(links[3]).Target);
        Assert.Equal("누름틀", Assert.IsType<HText>(links[4]).Text); // other fields keep their text

        var bullet = Assert.IsType<HParagraph>(blocks[3]).List!;
        Assert.Equal(("●", true), (bullet.Numbering.Levels[0].Text, bullet.Numbering.Levels[0].Bullet)); // private-use bullet
        var numbered = Assert.IsType<HParagraph>(blocks[4]);
        Assert.Equal((1, "(^2)", "ROMAN_SMALL", 3), (numbered.List!.Level, numbered.List.Numbering.Levels[1].Text, numbered.List.Numbering.Levels[1].Format, numbered.List.Numbering.Levels[1].Start));
        Assert.True(numbered.PageBreakBefore);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, 1500), Assert.IsType<HParagraph>(blocks[5]).Format.LineSpacing);

        // A table inside a paragraph splits it: text before, the table, text after.
        Assert.Equal("표 앞", Text([blocks[6]]));
        var table = Assert.IsType<HTable>(blocks[7]);
        Assert.Equal("표 뒤", Text([blocks[8]]));
        Assert.Equal((HAlign.Right, new HInsets(141, 141, 141, 141)), (table.Align, table.CellMargin));
        Assert.Equal([8000, 12000, 10000], table.ColumnWidths!);
        Assert.Equal("표 1 캡션", Text(table.Caption));
        var merged = table.Rows[0].Cells[0];
        Assert.Equal((0, 1, 2, "#FFFF00", HVerticalAlign.Center), (merged.Column, merged.RowSpan, merged.ColSpan, merged.Fill, merged.VerticalAlign));
        Assert.Equal(new HBorders(new HBorder(HBorderStyle.Solid, 0.12, "#000000"), new HBorder(HBorderStyle.Double, 0.5, "#FF0000"),
            new HBorder(HBorderStyle.Dash, 0.2, "#0000FF"), new HBorder(HBorderStyle.Dot, 0.12, "#000000")), merged.Borders);
        Assert.Equal((2, 2, 1, HVerticalAlign.Bottom), (table.Rows[0].Cells[1].Column, table.Rows[0].Cells[1].RowSpan, table.Rows[0].Cells[1].ColSpan, table.Rows[0].Cells[1].VerticalAlign));
        Assert.Equal([true, false], table.Rows.Select(r => r.Header));
        Assert.Equal([1500, 1500], table.Rows.Select(r => r.Height));
        Assert.Equal("안쪽", Text([Assert.IsType<HTable>(Assert.Single(table.Rows[1].Cells[0].Blocks))]));

        var pictures = Assert.IsType<HParagraph>(blocks[9]).Inlines.OfType<HImage>().ToList();
        Assert.Equal(2, pictures.Count); // the third refers to a missing image
        Assert.Equal((5000, 2500), (pictures[0].Width, pictures[0].Height));
        Assert.Equal(new HAnchor("COLUMN", "LEFT", 1000, "PARA", "TOP", -100, "SQUARE", false), pictures[0].Anchor);
        Assert.Null(pictures[1].Anchor);
        Assert.All(pictures, p => Assert.True(new MagickImageInfo(p.Path).Width > 0));

        // Group members float at their place in the group.
        var group = Assert.IsType<HParagraph>(blocks[10]).Inlines;
        var box = Assert.IsType<HTextBox>(group[0]);
        Assert.Equal((12000, 2000, 2000, 2500), (box.Width, box.Height, box.Anchor!.HorizontalOffset, box.Anchor.VerticalOffset));
        Assert.Equal(("#FF0000,#0000FF", 90, HBorderStyle.Dash, 20), (string.Join(",", box.Gradient!.Colors), box.Gradient.Angle, box.Line.Style, box.CornerRatio));
        Assert.Equal((new HInsets(100, 100, 50, 50), HVerticalAlign.Center, "묶음 글상자"), (box.Padding, box.VerticalAlign, Text(box.Blocks)));
        var ellipse = Assert.IsType<HShape>(group[1]);
        Assert.Equal((HShapeKind.Ellipse, 6000, 13000, "#00FF00", 0.1), (ellipse.Kind, ellipse.Width, ellipse.Anchor!.HorizontalOffset, ellipse.Fill, ellipse.Line.WidthMm));

        Assert.Equal("식: x over y 12본말", Text([blocks[11]])); // equation script, overlapping characters, ruby base text
        var notes = Assert.IsType<HParagraph>(blocks[12]).Inlines.OfType<HNote>().ToList();
        Assert.Equal([("각주 내용", false), ("미주 내용", true)], notes.Select(n => (Text(n.Blocks), n.Endnote)));
        Assert.Equal("차트 뒤", Text([blocks[13]]));
        Assert.True(Assert.IsType<HParagraph>(blocks[14]).ColumnBreakBefore);

        // Other prefixes (a default namespace) in the second section; hp:newNum restarts page numbers.
        var second = document.Sections[1];
        Assert.Equal((59528, 84188, false), (second.Page!.Width, second.Page.Height, second.Page.Landscape));
        Assert.Equal(3, second.StartPageNumber);
        Assert.Equal("둘째 구역", Text(second.Blocks));
        Assert.True(((HText)((HParagraph)second.Blocks[0]).Inlines[0]).Format.Bold);

        Assert.Equal(new Dictionary<string, int> { ["ole"] = 1, ["pic"] = 1 }, skipped);
    }

    private static HParagraph ParagraphOf(IEnumerable<HInline> inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    // ───────────────────────── Errors and speed ─────────────────────────

    [Fact]
    public void Protected_and_foreign_files_give_a_clear_error()
    {
        var dir = fx.NewFolder();
        var media = Path.Combine(dir, "media");

        var encrypted = Path.Combine(dir, "encrypted.hwpx");
        using (var zip = ZipFile.Open(encrypted, ZipArchiveMode.Create))
        {
            Write(zip, "mimetype", "application/hwp+zip");
            Write(zip, "META-INF/manifest.xml",
                "<odf:manifest xmlns:odf=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"><odf:file-entry odf:full-path=\"Contents/section0.xml\" odf:media-type=\"application/xml\">" +
                "<odf:encryption-data odf:checksum-type=\"SHA1\" odf:checksum=\"AAAA\"><odf:algorithm odf:algorithm-name=\"AES\"/></odf:encryption-data></odf:file-entry></odf:manifest>");
            Write(zip, "Contents/section0.xml", "\u0001\u0002 not xml");
        }
        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => HwpxReader.Read(encrypted, media)).Message);

        var drm = Path.Combine(dir, "drm.hwpx");
        File.WriteAllBytes(drm, Encoding.ASCII.GetBytes("<DRMONE>This is a protected document</DRMONE>"));
        Assert.Contains("DRM", Assert.Throws<InvalidDataException>(() => HwpxReader.Read(drm, media)).Message);

        var hwp = Path.Combine(dir, "binary.hwpx");
        File.WriteAllBytes(hwp, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        Assert.Contains("HWP 5.0", Assert.Throws<InvalidDataException>(() => HwpxReader.Read(hwp, media)).Message);

        var broken = Path.Combine(dir, "broken.hwpx");
        using (var zip = ZipFile.Open(broken, ZipArchiveMode.Create))
        {
            Write(zip, "mimetype", "application/hwp+zip");
            Write(zip, "Contents/header.xml", "<hh:head xmlns:hh=\"http://www.hancom.co.kr/hwpml/2011/head\"/>");
            Write(zip, "Contents/section0.xml", "PK\u0003\u0004 encrypted bytes");
        }
        Assert.Contains("encrypted", Assert.Throws<InvalidDataException>(() => HwpxReader.Read(broken, media)).Message);
    }

    [Fact]
    public void A_hundred_pages_are_read_in_well_under_a_second()
    {
        // About 100 A4 pages: 1 300 paragraphs of three lines with mixed formatting, and 26 tables of 10 rows.
        var section = new HSection();
        var bold = new HCharFormat(Bold: true, Size: 1100);
        for (var i = 0; i < 1300; i++)
        {
            var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Justify, After: 400) };
            paragraph.Inlines.Add(new HText($"{i + 1}. ", bold));
            paragraph.Inlines.Add(new HText(string.Concat(Enumerable.Repeat("한글 문서를 읽는 속도를 확인하는 문장입니다. ", 4)), default));
            paragraph.Inlines.Add(new HText("강조", new HCharFormat(Italic: true, Color: "#C00000")));
            section.Blocks.Add(paragraph);
            if (i % 50 == 49)
            {
                var table = new HTable { ColumnCount = 5 };
                for (var r = 0; r < 10; r++)
                {
                    var row = new HRow();
                    for (var c = 0; c < 5; c++)
                    {
                        var cell = new HCell();
                        var text = new HParagraph();
                        text.Inlines.Add(new HText($"셀 {r},{c}", default));
                        cell.Blocks.Add(text);
                        row.Cells.Add(cell);
                    }
                    table.Rows.Add(row);
                }
                section.Blocks.Add(table);
            }
        }
        var document = new HDocument();
        document.Sections.Add(section);
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "long.hwpx");
        HwpxWriter.Write(document, path);

        HwpxReader.Read(path, Path.Combine(dir, "warm-up")); // JIT
        var best = TimeSpan.MaxValue;
        HDocument? read = null;
        for (var run = 0; run < 5; run++)
        {
            var watch = Stopwatch.StartNew();
            read = HwpxReader.Read(path, Path.Combine(dir, $"media-{run}"));
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.Equal(1326, read!.Sections.Single().Blocks.Count);
        // Measured in a debug build while other tests run in parallel. Most of the time is parsing the 1.5 MB section.
        Assert.True(best < TimeSpan.FromSeconds(1), $"Reading took {best.TotalMilliseconds:0} ms");
    }

    // ───────────────────────── A package like 한글 writes it ─────────────────────────

    private static void Write(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>
    /// A two-section package with the features and variants of files saved by 한글: hp:switch branches (exact and
    /// doubled values, a 2016 heading), bullets, outline numbering, automatic page numbers, fields, highlighter,
    /// tracked changes, special spaces, a caption, nested and merged cells, pictures (one without a manifest entry,
    /// one missing), a group, equation, compose, ruby text, notes, a chart with an OLE fallback, and in the second
    /// section other namespace prefixes.
    /// </summary>
    private static string HangulStylePackage(string folder)
    {
        const string Namespaces =
            "xmlns:ha=\"http://www.hancom.co.kr/hwpml/2011/app\" xmlns:hp=\"http://www.hancom.co.kr/hwpml/2011/paragraph\" " +
            "xmlns:hp10=\"http://www.hancom.co.kr/hwpml/2016/paragraph\" xmlns:hs=\"http://www.hancom.co.kr/hwpml/2011/section\" " +
            "xmlns:hc=\"http://www.hancom.co.kr/hwpml/2011/core\" xmlns:hh=\"http://www.hancom.co.kr/hwpml/2011/head\"";
        const string Case = "<hp:case hp:required-namespace=\"http://www.hancom.co.kr/hwpml/2016/HwpUnitChar\">";
        static string Border(string type, string width, string color, string fill = "") =>
            "<hh:slash type=\"NONE\" Crooked=\"0\" isCounter=\"0\"/><hh:backSlash type=\"NONE\" Crooked=\"0\" isCounter=\"0\"/>" + type + width + color + fill;
        static string CharPr(string id, string attributes, string fontRef, string spacing, string flags) =>
            $"<hh:charPr id=\"{id}\" {attributes} useFontSpace=\"0\" useKerning=\"0\" symMark=\"NONE\" borderFillIDRef=\"1\">" +
            $"<hh:fontRef hangul=\"{fontRef}\" latin=\"{fontRef}\" hanja=\"0\" japanese=\"0\" other=\"0\" symbol=\"0\" user=\"0\"/>" +
            "<hh:ratio hangul=\"100\" latin=\"100\" hanja=\"100\" japanese=\"100\" other=\"100\" symbol=\"100\" user=\"100\"/>" +
            $"<hh:spacing hangul=\"{spacing}\" latin=\"{spacing}\" hanja=\"0\" japanese=\"0\" other=\"0\" symbol=\"0\" user=\"0\"/>" +
            "<hh:relSz hangul=\"100\" latin=\"100\" hanja=\"100\" japanese=\"100\" other=\"100\" symbol=\"100\" user=\"100\"/>" +
            "<hh:offset hangul=\"0\" latin=\"0\" hanja=\"0\" japanese=\"0\" other=\"0\" symbol=\"0\" user=\"0\"/>" + flags + "</hh:charPr>";
        static string Margins(int intent, int left, int right, int prev, int next) =>
            $"<hh:margin><hc:intent value=\"{intent}\" unit=\"HWPUNIT\"/><hc:left value=\"{left}\" unit=\"HWPUNIT\"/><hc:right value=\"{right}\" unit=\"HWPUNIT\"/><hc:prev value=\"{prev}\" unit=\"HWPUNIT\"/><hc:next value=\"{next}\" unit=\"HWPUNIT\"/></hh:margin>";
        static string ParaPr(string id, string tabPr, string align, string heading, string breaks, string spacing) =>
            $"<hh:paraPr id=\"{id}\" tabPrIDRef=\"{tabPr}\" condense=\"0\" fontLineHeight=\"0\" snapToGrid=\"1\" suppressLineNumbers=\"0\" checked=\"0\">" +
            $"<hh:align horizontal=\"{align}\" vertical=\"BASELINE\"/>{heading}" +
            $"<hh:breakSetting breakLatinWord=\"KEEP_WORD\" breakNonLatinWord=\"KEEP_WORD\" {breaks} lineWrap=\"BREAK\"/>" +
            $"<hh:autoSpacing eAsianEng=\"0\" eAsianNum=\"0\"/>{spacing}<hh:border borderFillIDRef=\"1\" offsetLeft=\"0\" offsetRight=\"0\" offsetTop=\"0\" offsetBottom=\"0\" connect=\"0\" ignoreMargin=\"0\"/></hh:paraPr>";
        static string Switch(string exact, string doubled) => $"<hp:switch>{Case}{exact}</hp:case><hp:default>{doubled}</hp:default></hp:switch>";
        const string NoBreaks = "widowOrphan=\"0\" keepWithNext=\"0\" keepLines=\"0\" pageBreakBefore=\"0\"";
        const string Percent160 = "<hh:lineSpacing type=\"PERCENT\" value=\"160\" unit=\"HWPUNIT\"/>";
        static string Heading(string type, int id, int level) => $"<hh:heading type=\"{type}\" idRef=\"{id}\" level=\"{level}\"/>";
        static string ParaHead(int level, string format, string text, int start = 1) =>
            $"<hh:paraHead start=\"{start}\" level=\"{level}\" align=\"LEFT\" useInstWidth=\"1\" autoIndent=\"1\" widthAdjust=\"0\" textOffsetType=\"PERCENT\" textOffset=\"50\" numFormat=\"{format}\" charPrIDRef=\"4294967295\" checkable=\"0\">{text}</hh:paraHead>";

        var header =
            $"<hh:head {Namespaces} version=\"1.4\" secCnt=\"2\"><hh:beginNum page=\"1\" footnote=\"1\" endnote=\"1\" pic=\"1\" tbl=\"1\" equation=\"1\"/><hh:refList>" +
            "<hh:fontfaces itemCnt=\"2\">" +
            "<hh:fontface lang=\"HANGUL\" fontCnt=\"2\"><hh:font id=\"0\" face=\"함초롬바탕\" type=\"TTF\" isEmbedded=\"0\"><hh:typeInfo familyType=\"FCAT_GOTHIC\" weight=\"6\" proportion=\"4\" contrast=\"0\" strokeVariation=\"1\" armStyle=\"1\" letterform=\"1\" midline=\"1\" xHeight=\"1\"/></hh:font><hh:font id=\"1\" face=\"맑은 고딕\" type=\"TTF\" isEmbedded=\"0\"/></hh:fontface>" +
            "<hh:fontface lang=\"LATIN\" fontCnt=\"2\"><hh:font id=\"0\" face=\"함초롬바탕\" type=\"TTF\" isEmbedded=\"0\"/><hh:font id=\"1\" face=\"Times New Roman\" type=\"TTF\" isEmbedded=\"0\"/></hh:fontface>" +
            "</hh:fontfaces><hh:borderFills itemCnt=\"2\">" +
            "<hh:borderFill id=\"1\" threeD=\"0\" shadow=\"0\" centerLine=\"NONE\" breakCellSeparateLine=\"0\">" +
            Border("<hh:leftBorder type=\"NONE\" width=\"0.1 mm\" color=\"#000000\"/><hh:rightBorder type=\"NONE\" width=\"0.1 mm\" color=\"#000000\"/>",
                "<hh:topBorder type=\"NONE\" width=\"0.1 mm\" color=\"#000000\"/><hh:bottomBorder type=\"NONE\" width=\"0.1 mm\" color=\"#000000\"/>",
                "<hh:diagonal type=\"SOLID\" width=\"0.1 mm\" color=\"#000000\"/>") + "</hh:borderFill>" +
            "<hh:borderFill id=\"2\" threeD=\"0\" shadow=\"0\" centerLine=\"NONE\" breakCellSeparateLine=\"0\">" +
            Border("<hh:leftBorder type=\"SOLID\" width=\"0.12 mm\" color=\"#000000\"/><hh:rightBorder type=\"DOUBLE_SLIM\" width=\"0.5 mm\" color=\"#FF0000\"/>",
                "<hh:topBorder type=\"DASH\" width=\"0.2 mm\" color=\"#0000FF\"/><hh:bottomBorder type=\"DOT\" width=\"0.12mm\" color=\"#000000\"/>",
                "<hh:diagonal type=\"SOLID\" width=\"0.1 mm\" color=\"#000000\"/>",
                "<hc:fillBrush><hc:winBrush faceColor=\"#FFFF00\" hatchColor=\"#000000\" alpha=\"0\"/></hc:fillBrush>") + "</hh:borderFill>" +
            "</hh:borderFills><hh:charProperties itemCnt=\"2\">" +
            CharPr("0", "height=\"1000\" textColor=\"#000000\" shadeColor=\"none\"", "0", "0",
                "<hh:underline type=\"NONE\" shape=\"SOLID\" color=\"#000000\"/><hh:strikeout shape=\"NONE\" color=\"#000000\"/><hh:outline type=\"NONE\"/><hh:shadow type=\"NONE\" color=\"#B2B2B2\" offsetX=\"10\" offsetY=\"10\"/>") +
            CharPr("1", "height=\"1400\" textColor=\"#0000ff\" shadeColor=\"#FFFF00\"", "1", "-5",
                "<hh:bold/><hh:italic/><hh:underline type=\"BOTTOM\" shape=\"SOLID\" color=\"#0000FF\"/><hh:strikeout shape=\"SOLID\" color=\"#0000FF\"/><hh:outline type=\"NONE\"/><hh:shadow type=\"NONE\" color=\"#B2B2B2\" offsetX=\"10\" offsetY=\"10\"/><hh:supscript/>") +
            "</hh:charProperties><hh:tabProperties itemCnt=\"2\"><hh:tabPr id=\"0\" autoTabLeft=\"0\" autoTabRight=\"0\"/><hh:tabPr id=\"1\" autoTabLeft=\"0\" autoTabRight=\"0\">" +
            Switch("<hh:tabItem pos=\"20000\" type=\"CENTER\" leader=\"DASH\" unit=\"HWPUNIT\"/>", "<hh:tabItem pos=\"40000\" type=\"CENTER\" leader=\"DASH\"/>") +
            "</hh:tabPr></hh:tabProperties><hh:numberings itemCnt=\"2\">" +
            "<hh:numbering id=\"1\" start=\"0\">" + ParaHead(1, "DIGIT", "^1.") + ParaHead(2, "HANGUL_SYLLABLE", "^2.") + "</hh:numbering>" +
            "<hh:numbering id=\"2\" start=\"1\">" + ParaHead(1, "DIGIT", "^1)") + ParaHead(2, "ROMAN_SMALL", "(^2)", start: 3) + "</hh:numbering>" +
            "</hh:numberings><hh:bullets itemCnt=\"1\"><hh:bullet id=\"1\" char=\"\" useImage=\"0\">" +
            "<hh:paraHead level=\"0\" align=\"LEFT\" useInstWidth=\"0\" autoIndent=\"1\" widthAdjust=\"0\" textOffsetType=\"PERCENT\" textOffset=\"50\" numFormat=\"DIGIT\" charPrIDRef=\"4294967295\" checkable=\"0\"/></hh:bullet></hh:bullets>" +
            "<hh:paraProperties itemCnt=\"6\">" +
            ParaPr("0", "0", "JUSTIFY", Heading("NONE", 0, 0), NoBreaks, Switch(Margins(0, 0, 0, 0, 0) + Percent160, Margins(0, 0, 0, 0, 0) + Percent160)) +
            ParaPr("1", "1", "LEFT", Heading("NONE", 0, 0), "widowOrphan=\"1\" keepWithNext=\"1\" keepLines=\"1\" pageBreakBefore=\"0\"",
                Switch(Margins(-2000, 1000, 500, 400, 600) + "<hh:lineSpacing type=\"FIXED\" value=\"1800\" unit=\"HWPUNIT\"/>",
                    Margins(-4000, 2000, 1000, 800, 1200) + "<hh:lineSpacing type=\"FIXED\" value=\"3600\" unit=\"HWPUNIT\"/>")) +
            // An older paragraph shape without a switch: lengths in the doubled scale.
            ParaPr("2", "0", "LEFT", Heading("OUTLINE", 0, 0), NoBreaks, Margins(0, 2000, 0, 0, 0) + "<hh:lineSpacing type=\"PERCENT\" value=\"150\"/>") +
            ParaPr("3", "0", "LEFT", Heading("BULLET", 1, 0), NoBreaks, Switch(Margins(0, 0, 0, 0, 0) + Percent160, Margins(0, 0, 0, 0, 0) + Percent160)) +
            ParaPr("4", "0", "LEFT",
                "<hp:switch><hp:case hp:required-namespace=\"http://www.hancom.co.kr/hwpml/2016/paragraph\">" + Heading("NUMBER", 2, 1) + "</hp:case><hp:default>" + Heading("NONE", 0, 0) + "</hp:default></hp:switch>",
                "widowOrphan=\"0\" keepWithNext=\"0\" keepLines=\"0\" pageBreakBefore=\"1\"", Switch(Margins(0, 0, 0, 0, 0) + Percent160, Margins(0, 0, 0, 0, 0) + Percent160)) +
            ParaPr("5", "0", "LEFT", Heading("NONE", 0, 0), NoBreaks,
                Switch(Margins(0, 0, 0, 0, 0) + "<hh:lineSpacing type=\"BETWEEN_LINES\" value=\"500\" unit=\"HWPUNIT\"/>", Margins(0, 0, 0, 0, 0) + "<hh:lineSpacing type=\"BETWEEN_LINES\" value=\"1000\" unit=\"HWPUNIT\"/>")) +
            "</hh:paraProperties><hh:styles itemCnt=\"2\">" +
            "<hh:style id=\"0\" type=\"PARA\" name=\"바탕글\" engName=\"Normal\" paraPrIDRef=\"0\" charPrIDRef=\"0\" nextStyleIDRef=\"0\" langID=\"1042\" lockForm=\"0\"/>" +
            "<hh:style id=\"1\" type=\"PARA\" name=\"개요 1\" engName=\"Outline 1\" paraPrIDRef=\"2\" charPrIDRef=\"0\" nextStyleIDRef=\"1\" langID=\"1042\" lockForm=\"0\"/>" +
            "</hh:styles></hh:refList><hh:compatibleDocument targetProgram=\"HWP201X\"><hh:layoutCompatibility/></hh:compatibleDocument></hh:head>";

        static string P(string paraPr, string runs, bool columnBreak = false) =>
            $"<hp:p id=\"0\" paraPrIDRef=\"{paraPr}\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"{(columnBreak ? 1 : 0)}\" merged=\"0\">{runs}" +
            "<hp:linesegarray><hp:lineseg textpos=\"0\" vertpos=\"0\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"393216\"/></hp:linesegarray></hp:p>";
        static string R(string content, string charPr = "0") => $"<hp:run charPrIDRef=\"{charPr}\">{content}</hp:run>";
        static string T(string text) => $"<hp:t>{text}</hp:t>";
        static string SubList(string paragraphs, string vertAlign = "TOP") =>
            $"<hp:subList id=\"\" textDirection=\"HORIZONTAL\" lineWrap=\"BREAK\" vertAlign=\"{vertAlign}\" linkListIDRef=\"0\" linkListNextIDRef=\"0\" textWidth=\"0\" textHeight=\"0\" hasTextRef=\"0\" hasNumRef=\"0\">{paragraphs}</hp:subList>";
        static string Cell(string content, int column, int row, int colSpan, int rowSpan, int width, int height, string borderFill = "1", bool header = false, string vertAlign = "TOP") =>
            $"<hp:tc name=\"\" header=\"{(header ? 1 : 0)}\" hasMargin=\"0\" protect=\"0\" editable=\"0\" dirty=\"0\" borderFillIDRef=\"{borderFill}\">{SubList(content, vertAlign)}" +
            $"<hp:cellAddr colAddr=\"{column}\" rowAddr=\"{row}\"/><hp:cellSpan colSpan=\"{colSpan}\" rowSpan=\"{rowSpan}\"/><hp:cellSz width=\"{width}\" height=\"{height}\"/>" +
            "<hp:cellMargin left=\"141\" right=\"141\" top=\"141\" bottom=\"141\"/></hp:tc>";
        static string Pos(bool inline, int x, int y, string horzAlign = "LEFT") =>
            $"<hp:pos treatAsChar=\"{(inline ? 1 : 0)}\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"0\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"{horzAlign}\" vertOffset=\"{y}\" horzOffset=\"{x}\"/>";
        static string Picture(string item, int width, int height, string pos) =>
            $"<hp:pic id=\"5\" zOrder=\"0\" numberingType=\"PICTURE\" textWrap=\"SQUARE\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"6\" reverse=\"0\">" +
            $"<hp:offset x=\"0\" y=\"0\"/><hp:orgSz width=\"{width}\" height=\"{height}\"/><hp:curSz width=\"{width}\" height=\"{height}\"/><hp:flip horizontal=\"0\" vertical=\"0\"/>" +
            $"<hp:imgRect><hc:pt0 x=\"0\" y=\"0\"/><hc:pt1 x=\"{width}\" y=\"0\"/><hc:pt2 x=\"{width}\" y=\"{height}\"/><hc:pt3 x=\"0\" y=\"{height}\"/></hp:imgRect>" +
            $"<hp:imgClip left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:inMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:imgDim dimwidth=\"0\" dimheight=\"0\"/>" +
            $"<hc:img binaryItemIDRef=\"{item}\" bright=\"0\" contrast=\"0\" effect=\"REAL_PIC\" alpha=\"0\"/><hp:effects/>" +
            $"<hp:sz width=\"{width}\" widthRelTo=\"ABSOLUTE\" height=\"{height}\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>{pos}<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:pic>";
        static string Note(string kind, string text) =>
            $"<hp:ctrl><hp:{kind} number=\"1\" suffixChar=\"41\" instId=\"77\">" +
            SubList(P("0", R($"<hp:ctrl><hp:autoNum num=\"1\" numType=\"{(kind == "footNote" ? "FOOTNOTE" : "ENDNOTE")}\"><hp:autoNumFormat type=\"DIGIT\" userChar=\"\" prefixChar=\"\" suffixChar=\")\" supscript=\"0\"/></hp:autoNum></hp:ctrl>" + T(" " + text)))) +
            $"</hp:{kind}></hp:ctrl>";
        static string Hyperlink(string id, string command) =>
            $"<hp:ctrl><hp:fieldBegin id=\"{id}\" type=\"HYPERLINK\" name=\"\" editable=\"0\" dirty=\"0\" zorder=\"-1\" fieldid=\"627600491\"><hp:parameters cnt=\"2\" name=\"\">" +
            $"<hp:integerParam name=\"Prop\">0</hp:integerParam><hp:stringParam name=\"Command\">{command}</hp:stringParam></hp:parameters></hp:fieldBegin></hp:ctrl>";
        static string FieldEnd(string id) => $"<hp:ctrl><hp:fieldEnd beginIDRef=\"{id}\" fieldid=\"627600491\"/></hp:ctrl>";

        var secPr =
            "<hp:secPr id=\"\" textDirection=\"HORIZONTAL\" spaceColumns=\"1134\" tabStop=\"8000\" tabStopVal=\"4000\" tabStopUnit=\"HWPUNIT\" outlineShapeIDRef=\"1\" memoShapeIDRef=\"0\" textVerticalWidthHead=\"0\" masterPageCnt=\"0\">" +
            "<hp:grid lineGrid=\"0\" charGrid=\"0\" wonggojiFormat=\"0\"/><hp:startNum pageStartsOn=\"BOTH\" page=\"5\" pic=\"0\" tbl=\"0\" equation=\"0\"/>" +
            "<hp:visibility hideFirstHeader=\"1\" hideFirstFooter=\"0\" hideFirstMasterPage=\"0\" border=\"SHOW_ALL\" fill=\"SHOW_ALL\" hideFirstPageNum=\"0\" hideFirstEmptyLine=\"0\" showLineNumber=\"0\"/>" +
            "<hp:lineNumberShape restartType=\"0\" countBy=\"0\" distance=\"0\" startNumber=\"0\"/>" +
            "<hp:pagePr landscape=\"NARROWLY\" width=\"59528\" height=\"84188\" gutterType=\"LEFT_ONLY\"><hp:margin header=\"4252\" footer=\"4252\" gutter=\"0\" left=\"8504\" right=\"8504\" top=\"5668\" bottom=\"4252\"/></hp:pagePr>" +
            "<hp:footNotePr><hp:autoNumFormat type=\"DIGIT\" userChar=\"\" prefixChar=\"\" suffixChar=\")\" supscript=\"0\"/><hp:noteLine length=\"-1\" type=\"SOLID\" width=\"0.12 mm\" color=\"#000000\"/></hp:footNotePr>" +
            "</hp:secPr>";
        var setup = secPr +
            "<hp:ctrl><hp:colPr id=\"\" type=\"NEWSPAPER\" layout=\"LEFT\" colCount=\"2\" sameSz=\"0\" sameGap=\"0\"><hp:colLine type=\"SOLID\" width=\"0.12 mm\" color=\"#000000\"/><hp:colSz width=\"20000\" gap=\"850\"/><hp:colSz width=\"21000\" gap=\"0\"/></hp:colPr></hp:ctrl>";
        var headerControls =
            "<hp:ctrl><hp:header id=\"1\" applyPageType=\"ODD\">" + SubList(P("0", R(T("홀수 쪽 머리말")))) + "</hp:header></hp:ctrl>" +
            "<hp:ctrl><hp:pageNum pos=\"BOTTOM_CENTER\" formatType=\"DIGIT\" sideChar=\"-\"/></hp:ctrl>";
        var nestedTable =
            "<hp:tbl id=\"21\" zOrder=\"1\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"CELL\" repeatHeader=\"0\" rowCnt=\"1\" colCnt=\"1\" cellSpacing=\"0\" borderFillIDRef=\"1\" noAdjust=\"0\">" +
            "<hp:sz width=\"6000\" widthRelTo=\"ABSOLUTE\" height=\"1000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" + Pos(true, 0, 0) + "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/>" +
            "<hp:tr>" + Cell(P("0", R(T("안쪽"))), 0, 0, 1, 1, 6000, 1000) + "</hp:tr></hp:tbl>";
        var table =
            "<hp:tbl id=\"20\" zOrder=\"0\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"CELL\" repeatHeader=\"1\" rowCnt=\"2\" colCnt=\"3\" cellSpacing=\"0\" borderFillIDRef=\"1\" noAdjust=\"0\">" +
            "<hp:sz width=\"30000\" widthRelTo=\"ABSOLUTE\" height=\"3000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" + Pos(false, 0, 0, "RIGHT") +
            "<hp:outMargin left=\"283\" right=\"283\" top=\"283\" bottom=\"283\"/><hp:inMargin left=\"141\" right=\"141\" top=\"141\" bottom=\"141\"/>" +
            "<hp:caption side=\"BOTTOM\" fullSz=\"0\" width=\"8504\" gap=\"850\" lastWidth=\"30000\">" +
            SubList(P("0", R(T("표 ") + "<hp:ctrl><hp:autoNum num=\"1\" numType=\"TABLE\"><hp:autoNumFormat type=\"DIGIT\" userChar=\"\" prefixChar=\"\" suffixChar=\"\" supscript=\"0\"/></hp:autoNum></hp:ctrl>" + T(" 캡션")))) + "</hp:caption>" +
            "<hp:tr>" + Cell(P("0", R(T("병합"))), 0, 0, 2, 1, 20000, 1500, "2", header: true, vertAlign: "CENTER") +
                Cell(P("0", R(T("세로"))), 2, 0, 1, 2, 10000, 3000, header: true, vertAlign: "BOTTOM") + "</hp:tr>" +
            "<hp:tr>" + Cell(P("0", R(nestedTable + "<hp:t/>")), 0, 1, 1, 1, 8000, 1500) + Cell(P("0", R(T("B"))), 1, 1, 1, 1, 12000, 1500) + "</hp:tr></hp:tbl>";
        var group =
            "<hp:container id=\"40\" zOrder=\"3\" numberingType=\"PICTURE\" textWrap=\"SQUARE\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"41\">" +
            "<hp:offset x=\"0\" y=\"0\"/><hp:orgSz width=\"20000\" height=\"10000\"/><hp:curSz width=\"20000\" height=\"10000\"/><hp:flip horizontal=\"0\" vertical=\"0\"/>" +
            "<hp:rect id=\"42\" zOrder=\"0\" numberingType=\"NONE\" textWrap=\"SQUARE\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"1\" instid=\"43\" ratio=\"20\">" +
            "<hp:offset x=\"1000\" y=\"2000\"/><hp:orgSz width=\"8000\" height=\"4000\"/><hp:curSz width=\"0\" height=\"0\"/><hp:flip horizontal=\"0\" vertical=\"0\"/>" +
            "<hp:renderingInfo><hc:transMatrix e1=\"1\" e2=\"0\" e3=\"1000\" e4=\"0\" e5=\"1\" e6=\"2000\"/><hc:scaMatrix e1=\"1.5\" e2=\"0\" e3=\"0\" e4=\"0\" e5=\"0.5\" e6=\"0\"/><hc:rotMatrix e1=\"1\" e2=\"0\" e3=\"0\" e4=\"0\" e5=\"1\" e6=\"0\"/></hp:renderingInfo>" +
            "<hp:lineShape color=\"#FF0000\" width=\"283\" style=\"DASH\" endCap=\"FLAT\" headStyle=\"NORMAL\" tailStyle=\"NORMAL\" headfill=\"1\" tailfill=\"1\" headSz=\"MEDIUM_MEDIUM\" tailSz=\"MEDIUM_MEDIUM\" outlineStyle=\"NORMAL\" alpha=\"0\"/>" +
            "<hc:fillBrush><hc:gradation type=\"LINEAR\" angle=\"90\" centerX=\"50\" centerY=\"50\" step=\"255\" colorNum=\"2\" stepCenter=\"50\" alpha=\"0\"><hc:color value=\"#FF0000\"/><hc:color value=\"#0000FF\"/></hc:gradation></hc:fillBrush>" +
            "<hp:shadow type=\"NONE\" color=\"#B2B2B2\" offsetX=\"0\" offsetY=\"0\" alpha=\"0\"/>" +
            "<hp:drawText lastWidth=\"8000\" name=\"\" editable=\"0\">" + SubList(P("0", R(T("묶음 글상자"))), "CENTER") + "<hp:textMargin left=\"100\" right=\"100\" top=\"50\" bottom=\"50\"/></hp:drawText>" +
            "<hc:pt0 x=\"0\" y=\"0\"/><hc:pt1 x=\"8000\" y=\"0\"/><hc:pt2 x=\"8000\" y=\"4000\"/><hc:pt3 x=\"0\" y=\"4000\"/></hp:rect>" +
            "<hp:ellipse id=\"44\" zOrder=\"1\" numberingType=\"NONE\" textWrap=\"SQUARE\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"1\" instid=\"45\" intervalDirty=\"0\" hasArcPr=\"0\" arcType=\"NORMAL\">" +
            "<hp:offset x=\"12000\" y=\"0\"/><hp:orgSz width=\"6000\" height=\"6000\"/><hp:curSz width=\"6000\" height=\"6000\"/>" +
            "<hp:lineShape color=\"#000000\" width=\"0\" style=\"SOLID\"/><hc:fillBrush><hc:winBrush faceColor=\"#00FF00\" hatchColor=\"#000000\" alpha=\"0\"/></hc:fillBrush></hp:ellipse>" +
            "<hp:sz width=\"20000\" widthRelTo=\"ABSOLUTE\" height=\"10000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" + Pos(false, 1000, 500) +
            "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:container>";

        var section0 =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><hs:sec {Namespaces}>" +
            $"<hp:p id=\"0\" paraPrIDRef=\"2\" styleIDRef=\"1\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\">{R(setup + headerControls)}{R(T("제1장 개요"))}</hp:p>" +
            P("1", R(T("굵게"), "1") + R(T("보통<hp:markpenBegin color=\"#00FF00\"/>형광<hp:markpenEnd/> 묶음<hp:nbSpace/>빈칸<hp:fwSpace/>고정<hp:tab width=\"4000\" leader=\"0\" type=\"1\"/>탭<hp:lineBreak/>둘째 줄" +
                "<hp:deleteBegin Id=\"1\" TcId=\"1\"/>지운 글<hp:deleteEnd Id=\"1\" TcId=\"1\"/><hp:insertBegin Id=\"2\" TcId=\"2\"/>넣은 글<hp:insertEnd Id=\"2\" TcId=\"2\"/>하이<hp:hyphen/>픈"))) +
            P("0", R("<hp:ctrl><hp:bookmark name=\"처음\"/></hp:ctrl>" + Hyperlink("10", "http\\://www.hancom.com\\?a=1;1;0;0;") + T("한컴")) +
                R(T("링크") + FieldEnd("10") + T(" 뒤"), "1") +
                R(Hyperlink("11", "?처음;0;0;0;") + T("처음으로") + FieldEnd("11") +
                  "<hp:ctrl><hp:fieldBegin id=\"12\" type=\"CLICK_HERE\" name=\"이름\" editable=\"1\" dirty=\"0\"><hp:parameters cnt=\"0\" name=\"\"/></hp:fieldBegin></hp:ctrl>" + T("누름틀") + FieldEnd("12"))) +
            P("3", R(T("글머리 항목"))) +
            P("4", R(T("번호 항목"))) +
            P("5", R(T("줄 간격"))) +
            P("0", R(T("표 앞") + table + T("표 뒤"))) +
            P("0", R(Picture("image1", 5000, 2500, Pos(false, 1000, unchecked((int)4294967196u)).Replace("vertOffset=\"-100\"", "vertOffset=\"4294967196\"", StringComparison.Ordinal)) +
                     Picture("image2", 3000, 3000, Pos(true, 0, 0)) + Picture("image9", 3000, 3000, Pos(true, 0, 0)) + "<hp:t/>")) +
            P("0", R(group + "<hp:t/>")) +
            P("0", R(T("식: ") +
                "<hp:equation id=\"30\" zOrder=\"4\" numberingType=\"EQUATION\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" version=\"Equation Version 60\" baseLine=\"86\" textColor=\"#000000\" baseUnit=\"1000\" lineMode=\"CHAR\" font=\"HYhwpEQ\">" +
                "<hp:sz width=\"2000\" widthRelTo=\"ABSOLUTE\" height=\"1500\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" + Pos(true, 0, 0) + "<hp:outMargin left=\"56\" right=\"56\" top=\"0\" bottom=\"0\"/><hp:shapeComment>수식입니다.</hp:shapeComment><hp:script>x over y</hp:script></hp:equation>" +
                T(" ") + "<hp:compose circleType=\"SHAPE_CIRCLE\" charSz=\"-3\" composeType=\"SPREAD\" charPrCnt=\"10\" composeText=\"12\"/>" +
                "<hp:dutmal posType=\"TOP\" szRatio=\"0\" option=\"4\" styleIDRef=\"0\" align=\"CENTER\"><hp:mainText>본말</hp:mainText><hp:subText>덧말</hp:subText></hp:dutmal>")) +
            P("0", R(T("각주 참조") + Note("footNote", "각주 내용") + Note("endNote", "미주 내용"))) +
            P("0", R("<hp:switch><hp:case hp:required-namespace=\"http://www.hancom.co.kr/hwpml/2016/ooxmlchart\"><hp:chart id=\"50\" chartIDRef=\"Chart/chart1.xml\"/></hp:case>" +
                     "<hp:default><hp:ole id=\"51\" objectType=\"EMBEDDED\" binaryItemIDRef=\"ole1\"/></hp:default></hp:switch>" + T("차트 뒤"))) +
            P("0", R(T("다음 단")), columnBreak: true) +
            "</hs:sec>";

        // Other prefixes: a default namespace for the section and "p:" for paragraphs.
        var section1 =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><sec xmlns=\"http://www.hancom.co.kr/hwpml/2011/section\" xmlns:p=\"http://www.hancom.co.kr/hwpml/2011/paragraph\">" +
            "<p:p id=\"0\" paraPrIDRef=\"0\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\"><p:run charPrIDRef=\"0\">" +
            "<p:secPr id=\"\" outlineShapeIDRef=\"1\"><p:startNum pageStartsOn=\"BOTH\" page=\"0\"/><p:pagePr landscape=\"WIDELY\" width=\"59528\" height=\"84188\" gutterType=\"LEFT_ONLY\">" +
            "<p:margin header=\"4252\" footer=\"4252\" gutter=\"0\" left=\"8504\" right=\"8504\" top=\"5668\" bottom=\"4252\"/></p:pagePr></p:secPr>" +
            "<p:ctrl><p:colPr id=\"\" type=\"NEWSPAPER\" layout=\"LEFT\" colCount=\"1\" sameSz=\"1\" sameGap=\"0\"/></p:ctrl><p:ctrl><p:newNum num=\"3\" numType=\"PAGE\"/></p:ctrl></p:run>" +
            "<p:run charPrIDRef=\"1\"><p:t>둘째 구역</p:t></p:run></p:p></sec>";

        var path = Path.Combine(folder, "hangul-style.hwpx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "mimetype", "application/hwp+zip", CompressionLevel.NoCompression);
        Write(zip, "version.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><hv:HCFVersion xmlns:hv=\"http://www.hancom.co.kr/hwpml/2011/version\" tagetApplication=\"WORDPROCESSOR\" major=\"5\" minor=\"1\" micro=\"1\" buildNumber=\"0\" os=\"1\" xmlVersion=\"1.4\" application=\"Hancom Office Hangul\" appVersion=\"12, 0, 0, 1234 WIN32LEWindows_10\"/>");
        Write(zip, "META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ocf:container xmlns:ocf=\"urn:oasis:names:tc:opendocument:xmlns:container\"><ocf:rootfiles>" +
            "<ocf:rootfile full-path=\"Contents/content.hpf\" media-type=\"application/hwpml-package+xml\"/><ocf:rootfile full-path=\"Preview/PrvText.txt\" media-type=\"text/plain\"/></ocf:rootfiles></ocf:container>");
        Write(zip, "META-INF/manifest.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><odf:manifest xmlns:odf=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"/>");
        Write(zip, "Contents/content.hpf",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><opf:package xmlns:opf=\"http://www.idpf.org/2007/opf/\" version=\"\" unique-identifier=\"\" id=\"\">" +
            "<opf:metadata><opf:title>한글 문서 제목</opf:title><opf:language>ko</opf:language></opf:metadata><opf:manifest>" +
            "<opf:item id=\"header\" href=\"Contents/header.xml\" media-type=\"application/xml\"/><opf:item id=\"image1\" href=\"BinData/image1.png\" media-type=\"image/png\" isEmbeded=\"1\"/>" +
            "<opf:item id=\"section0\" href=\"Contents/section0.xml\" media-type=\"application/xml\"/><opf:item id=\"section1\" href=\"Contents/section1.xml\" media-type=\"application/xml\"/>" +
            "<opf:item id=\"settings\" href=\"settings.xml\" media-type=\"application/xml\"/></opf:manifest>" +
            "<opf:spine><opf:itemref idref=\"header\" linear=\"yes\"/><opf:itemref idref=\"section0\" linear=\"yes\"/><opf:itemref idref=\"section1\" linear=\"yes\"/></opf:spine></opf:package>");
        Write(zip, "Contents/header.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>" + header);
        Write(zip, "Contents/section0.xml", section0);
        Write(zip, "Contents/section1.xml", section1);
        foreach (var (name, color) in new[] { ("image1", MagickColors.Coral), ("image2", MagickColors.Teal) })
        {
            using var stream = zip.CreateEntry($"BinData/{name}.png").Open();
            stream.Write(Png(color, 40, 20));
        }
        return path;
    }

    private sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
