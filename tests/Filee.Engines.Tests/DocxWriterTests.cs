// Anything → DOCX with the built-in writer (no Word, LibreOffice or Pandoc): every file is validated against the Office
// Open XML schemas (the element order Word insists on), read back with Filee's DOCX reader and, when LibreOffice is
// installed, converted to PDF by it as an independent reader. The round trip DOCX → model → DOCX → model checks that
// the layout the model carries survives (page setup, sections, headers, tables, objects, notes, formatting).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using ImageMagick;
using static Filee.Engines.Tests.DocxAssert;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocxWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice"];

    private RoutePlanner BuiltInPlanner() =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner();

    private async Task<string> ConvertAsync(string input, string target = "docx")
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Built_in_writer_is_preferred_for_docx()
    {
        var planner = fx.Catalog.CreatePlanner(["pandoc", "libreoffice"]);
        foreach (var from in new[] { "md", "txt", "xlsx", "csv", "pptx", "pdf" })
            Assert.Equal("docx-writer", Assert.Single(planner.Plan(from, "docx")!.Steps).Converter.Id);
        Assert.Equal("ooxml", Assert.Single(planner.Plan("docm", "docx")!.Steps).Converter.Id);
        Assert.DoesNotContain(fx.Converters.Single(c => c.Id == "docx-writer").Edges, e => e.From == "docx");
        // No optional engine needed.
        Assert.Equal("docx-writer", Assert.Single(BuiltInPlanner().Plan("md", "docx")!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Markdown_becomes_a_word_document_with_real_headings_lists_and_tables()
    {
        var dir = fx.NewFolder();
        using (var chart = new MagickImage(MagickColors.Teal, 160, 90))
            chart.Write(Path.Combine(dir, "chart.png"));
        var md = Path.Combine(dir, "회의록.md");
        await File.WriteAllTextAsync(md, """
            # 주간 회의

            오늘 결정한 것: **배포**, *문서 정리*, ~~취소~~, [웹 페이지](https://example.com/?q=한글).

            ## 할 일

            1. 설치 파일 만들기
               - 서명하기
            2. 문서 고치기

            | 항목 | 담당 |
            |------|------|
            | 설치 | 민지 |

            ![그림](chart.png)

            각주가 있는 문장.[^1]

            [^1]: 각주 내용
            """, TestContext.Current.CancellationToken);

        var docx = await ConvertAsync(md);

        ValidPackage(docx);
        var body = Body(docx);
        Assert.Equal(["Heading1", "Heading2"], body.Descendants(W + "pStyle").Select(s => (string)s.Attribute(W + "val")!).Where(s => s.StartsWith("Heading", StringComparison.Ordinal)));
        Assert.Contains(Body(docx, "word/styles.xml").Descendants(W + "name"), n => (string?)n.Attribute(W + "val") == "heading 1");

        var model = ReadBack(docx);
        var paragraphs = Paragraphs(model.Sections.SelectMany(s => s.Blocks)).ToList();
        Assert.Equal(1, paragraphs.Single(p => TextOf(p) == "주간 회의").HeadingLevel);
        Assert.Equal(2, paragraphs.Single(p => TextOf(p) == "할 일").HeadingLevel);

        var sentence = paragraphs.Single(p => TextOf(p).StartsWith("오늘 결정한 것", StringComparison.Ordinal));
        Assert.True(sentence.Inlines.OfType<HText>().Single(t => t.Text == "배포").Format.Bold);
        Assert.True(sentence.Inlines.OfType<HText>().Single(t => t.Text == "문서 정리").Format.Italic);
        Assert.True(sentence.Inlines.OfType<HText>().Single(t => t.Text == "취소").Format.Strike);
        Assert.Equal("https://example.com/?q=%ED%95%9C%EA%B8%80", sentence.Inlines.OfType<HLink>().Single().Target);

        // Lists are Word numbering, with the nested bullet on level 1.
        var first = paragraphs.Single(p => TextOf(p) == "설치 파일 만들기");
        Assert.Equal(("DIGIT", "^1.", 0), (first.List!.Numbering.Levels[0].Format, first.List.Numbering.Levels[0].Text, first.List.Level));
        var nested = paragraphs.Single(p => TextOf(p) == "서명하기");
        Assert.True(nested.List!.Numbering.Levels[1].Bullet);
        Assert.Equal(1, nested.List.Level);
        Assert.Same(first.List.Numbering, paragraphs.Single(p => TextOf(p) == "문서 고치기").List!.Numbering);

        var table = model.Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single();
        Assert.Equal((2, 2), (table.Rows.Count, table.ColumnCount));
        Assert.Equal("민지", TextOf((HParagraph)table.Rows[1].Cells[1].Blocks[0]));
        Assert.True(table.Rows[0].Header);

        var picture = paragraphs.SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.Equal((160 * 75, 90 * 75), (picture.Width, picture.Height)); // 96 dpi pixels
        var note = paragraphs.SelectMany(p => p.Inlines).OfType<HNote>().Single();
        Assert.Contains("각주 내용", string.Concat(note.Blocks.OfType<HParagraph>().Select(TextOf)));

        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(1, pages);
    }

    [Fact]
    public async Task Text_becomes_one_paragraph_per_line_on_an_a4_page()
    {
        var txt = Path.Combine(fx.NewFolder(), "메모.txt");
        await File.WriteAllTextAsync(txt, "첫 줄 한글\n\n文件转换 Hello\tTab", TestContext.Current.CancellationToken);

        var docx = await ConvertAsync(txt);

        ValidPackage(docx);
        var model = ReadBack(docx);
        var section = Assert.Single(model.Sections);
        Assert.Equal(["첫 줄 한글", "", "文件转换 HelloTab"], section.Blocks.OfType<HParagraph>().Select(TextOf));
        Assert.Single(((HParagraph)section.Blocks[2]).Inlines.OfType<HTab>());
        Assert.Equal((59530, 84190), (section.Page!.Width, section.Page.Height)); // A4 in twips × 5
        Assert.Equal(7200, section.Page.Left);
        await ConvertibleByLibreOfficeAsync(fx, docx);
    }

    [Fact]
    public async Task Spreadsheets_and_slides_become_tables_and_floating_objects()
    {
        var dir = fx.NewFolder();
        var xlsx = new XlsxBuilder();
        xlsx.Sheet("매출",
            XlsxBuilder.Row(1, xlsx.Text("A1", "지점별 매출", XlsxBuilder.Header)) +
            XlsxBuilder.Row(2, xlsx.Text("A2", "서울"), XlsxBuilder.Number("B2", 1350000, XlsxBuilder.Won)),
            after: "<mergeCells count=\"1\"><mergeCell ref=\"A1:B1\"/></mergeCells>");
        var sheetDocx = await ConvertAsync(xlsx.Save(Path.Combine(dir, "매출.xlsx")));

        ValidPackage(sheetDocx);
        var table = ReadBack(sheetDocx).Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single();
        var title = table.Rows[0].Cells[0];
        Assert.Equal((2, "#1F4E79"), (title.ColSpan, title.Fill));
        Assert.Equal("1,350,000원", TextOf((HParagraph)table.Rows[1].Cells[1].Blocks[0]));

        var pptx = new PptxBuilder();
        pptx.Slide(pptx.Title("사업 보고") + pptx.Shape("ellipse", 838200, 4400000, 3000000, 1500000, "#7F77DD", "강조"));
        pptx.Slide(pptx.Title("둘째 장"));
        var slidesDocx = await ConvertAsync(pptx.Save(Path.Combine(dir, "보고.pptx")));

        ValidPackage(slidesDocx);
        var slides = ReadBack(slidesDocx);
        var page = slides.Sections[0].Page!;
        Assert.True(page.Landscape);
        var boxes = slides.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HTextBox>().ToList();
        Assert.Contains(boxes, b => b.Shape == HShapeKind.Ellipse && b.Fill == "#7F77DD" && Paragraphs(b.Blocks).Any(p => TextOf(p) == "강조"));
        Assert.Contains(Body(slidesDocx).Descendants(), e => e.Name.LocalName == "prstGeom" && (string?)e.Attribute("prst") == "ellipse");
        Assert.All(boxes, b => Assert.NotNull(b.Anchor));
        Assert.Single(slides.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>(), p => p.PageBreakBefore);
        if (await ConvertibleByLibreOfficeAsync(fx, slidesDocx) is { } pages)
            Assert.Equal(2, pages);
    }

    /// <summary>A DOCX with most of what the model carries; read, written again by DocxWriter and read back.</summary>
    [Fact]
    public async Task Layout_survives_docx_to_model_to_docx()
    {
        var source = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="1">
                  <w:lvl w:ilvl="0"><w:start w:val="3"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="400" w:hanging="400"/></w:pPr></w:lvl>
                  <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="ganada"/><w:lvlText w:val="%2)"/><w:pPr><w:ind w:left="800" w:hanging="400"/></w:pPr></w:lvl>
                </w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                """,
        };
        using var image = new MagickImage(MagickColors.MediumPurple, 120, 60);
        var picture = source.Image(image.ToByteArray(MagickFormat.Png));
        var url = source.Hyperlink("https://example.com/?q=1");
        source.Footnotes("<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r>" + R(" 각주 설명") + "</w:p></w:footnote>");
        var header = source.Header(P(R("머리말") + "<w:r><w:tab/></w:r>" + Field("PAGE", "1"), "<w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9000\"/></w:tabs>"));
        var footer = source.Footer(P(Field("PAGE", "1") + R(" / ") + Field("NUMPAGES", "2"), "<w:jc w:val=\"center\"/>"));
        const string Shape = "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"rect\"/>" +
                             "<a:solidFill><a:srgbClr val=\"FFF2CC\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln></wps:spPr>" +
                             "<wps:txbx><w:txbxContent><w:p><w:r><w:t>글상자 안의 글</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr anchor=\"ctr\"/></wps:wsp>";
        var cell = (string content, string tcPr) => $"<w:tc><w:tcPr>{tcPr}</w:tcPr>{P(R(content))}</w:tc>";

        source.Paragraph(P("<w:bookmarkStart w:id=\"0\" w:name=\"intro\"/>" + R("큰 제목") + "<w:bookmarkEnd w:id=\"0\"/>", "<w:pStyle w:val=\"Heading1\"/><w:jc w:val=\"center\"/>"))
            .Paragraph(P(R("굵은 빨강", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"굴림\"/><w:b/><w:color w:val=\"FF0000\"/><w:spacing w:val=\"20\"/><w:sz w:val=\"28\"/>") +
                         R(" 형광펜", "<w:highlight w:val=\"yellow\"/>") + R(" 음영", "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"DDEEFF\"/>") +
                         R(" 위첨자", "<w:vertAlign w:val=\"superscript\"/>") + R(" 취소", "<w:strike/>") + R(" 밑줄", "<w:u w:val=\"single\"/>"),
                "<w:spacing w:before=\"240\" w:after=\"120\" w:line=\"320\" w:lineRule=\"exact\"/><w:ind w:left=\"720\" w:right=\"360\" w:hanging=\"360\"/><w:jc w:val=\"both\"/>"))
            .Paragraph(P(R("첫 항목"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("하위 항목"), "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("본문") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>" +
                         $"<w:hyperlink r:id=\"{url}\">" + R("웹 링크", "<w:rStyle w:val=\"Hyperlink\"/>") + "</w:hyperlink>" +
                         "<w:hyperlink w:anchor=\"intro\">" + R("소개로 이동") + "</w:hyperlink>"))
            .Paragraph("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:jc w:val=\"center\"/></w:tblPr>" +
                "<w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
                "<w:tr><w:trPr><w:tblHeader/></w:trPr>" + cell("가로 병합", "<w:gridSpan w:val=\"2\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"E6E6E6\"/>") +
                    cell("세로 병합", "<w:vMerge w:val=\"restart\"/><w:vAlign w:val=\"center\"/>") + "</w:tr>" +
                "<w:tr>" + cell("A", "") + cell("B", "") + cell("", "<w:vMerge/>") + "</w:tr>" +
                "<w:tr>" + cell("C", "<w:tcBorders><w:bottom w:val=\"double\" w:sz=\"12\" w:color=\"FF0000\"/></w:tcBorders>") + cell("D", "") + cell("E", "") + "</w:tr>" +
                "</w:tbl>")
            .Paragraph(P(InlinePicture(picture, 457200, 457200) +
                         Anchor(PictureUri, Picture(picture, 914400, 457200), 914400, 457200,
                             "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>",
                             "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>127000</wp:posOffset></wp:positionV>") +
                         Anchor(ShapeUri, Shape, 1828800, 914400, "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>",
                             "<wp:positionV relativeFrom=\"page\"><wp:posOffset>3600000</wp:posOffset></wp:positionV>", "<wp:wrapTopAndBottom/>")))
            .Paragraph(P(R("첫 구역 끝"), $"<w:sectPr><w:headerReference w:type=\"default\" r:id=\"{header}\"/><w:footerReference w:type=\"default\" r:id=\"{footer}\"/>" +
                "<w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/><w:pgMar w:top=\"1985\" w:right=\"1134\" w:bottom=\"1418\" w:left=\"1134\" w:header=\"851\" w:footer=\"851\" w:gutter=\"0\"/><w:pgNumType w:start=\"5\"/><w:titlePg/></w:sectPr>"))
            .Paragraph(P(R("한 단 본문"), $"<w:sectPr>{DefaultPage}</w:sectPr>"))
            .Paragraph(P(R("두 단 왼쪽")))
            .Paragraph(P(R("두 단 끝"), $"<w:sectPr><w:type w:val=\"continuous\"/>{DefaultPage}<w:cols w:num=\"2\" w:space=\"720\" w:sep=\"1\"/></w:sectPr>"))
            .Paragraph(P(R("앞 쪽") + "<w:r><w:br w:type=\"page\"/></w:r>" + R("다음 쪽")));
        source.SectionProperties = "<w:type w:val=\"continuous\"/>" + DefaultPage;
        var sourcePath = source.Save(Path.Combine(fx.NewFolder(), "원본.docx"));

        var media = Path.Combine(Path.GetDirectoryName(sourcePath)!, "media");
        var original = DocxReader.Read(sourcePath, media);
        var written = Path.Combine(Path.GetDirectoryName(sourcePath)!, "다시 쓴 문서.docx");
        DocxWriter.Write(original, written);
        ValidPackage(written);
        var copy = ReadBack(written);

        // Sections and page setup, columns changing within a page, headers and footers.
        Assert.Equal(original.Sections.Count, copy.Sections.Count);
        for (var s = 0; s < original.Sections.Count; s++)
        {
            Assert.Equal(original.Sections[s].Page, copy.Sections[s].Page);
            Assert.Equal(original.Sections[s].HideFirstHeader, copy.Sections[s].HideFirstHeader);
            Assert.Equal(original.Sections[s].StartPageNumber, copy.Sections[s].StartPageNumber);
        }
        var first = copy.Sections[0];
        Assert.True(first.Page!.Landscape);
        Assert.Equal(5, first.StartPageNumber);
        Assert.Equal("머리말", TextOf((HParagraph)first.Headers.Single().Blocks[0]).Trim());
        Assert.Single(((HParagraph)first.Headers[0].Blocks[0]).Inlines.OfType<HField>(), f => f.Kind == HFieldKind.PageNumber);
        Assert.Equal([HFieldKind.PageNumber, HFieldKind.TotalPages], ((HParagraph)first.Footers.Single().Blocks[0]).Inlines.OfType<HField>().Select(f => f.Kind));
        Assert.Equal(new HTabStop(45000, HTabKind.Right, "DOT"), ((HParagraph)first.Headers[0].Blocks[0]).Format.Tabs!.Single());
        Assert.Equal(2, copy.Sections.Count);
        var second = copy.Sections[1].Blocks.OfType<HParagraph>().ToList();
        Assert.Equal(new HColumns(2, 3600, true), second.Single(p => TextOf(p) == "두 단 왼쪽").ColumnsChange);
        Assert.Equal(HColumns.Single, second.Single(p => TextOf(p) == "앞 쪽").ColumnsChange);

        // Paragraph and character formatting.
        var originalParagraphs = Paragraphs(original.Sections.SelectMany(s => s.Blocks)).ToList();
        var paragraphs = Paragraphs(copy.Sections.SelectMany(s => s.Blocks)).ToList();
        HParagraph Find(List<HParagraph> list, string text) => list.First(p => TextOf(p).Contains(text, StringComparison.Ordinal));
        foreach (var text in new[] { "큰 제목", "굵은 빨강", "첫 항목", "본문", "다음 쪽" })
        {
            var (a, b) = (Find(originalParagraphs, text), Find(paragraphs, text));
            Assert.Equal(a.Format, b.Format);
            Assert.Equal(a.HeadingLevel, b.HeadingLevel);
            Assert.Equal(a.PageBreakBefore, b.PageBreakBefore);
        }
        var formatted = Find(paragraphs, "굵은 빨강");
        Assert.Equal((HAlign.Justify, 3600, -1800), (formatted.Format.Align, formatted.Format.Left, formatted.Format.FirstLine));
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Fixed, 1600), formatted.Format.LineSpacing);
        var runs = formatted.Inlines.OfType<HText>().ToList();
        Assert.Equal(Find(originalParagraphs, "굵은 빨강").Inlines.OfType<HText>().Select(t => t.Format), runs.Select(t => t.Format));
        Assert.Equal(new HCharFormat(true, false, false, false, false, false, 1400, "Arial", "굴림", "#FF0000", null, 7), runs[0].Format);
        Assert.Equal("#FFFF00", runs.Single(r => r.Text.Contains("형광펜")).Format.Shade);
        Assert.Equal("#DDEEFF", runs.Single(r => r.Text.Contains("음영")).Format.Shade);
        Assert.True(runs.Single(r => r.Text.Contains("위첨자")).Format.Superscript);
        Assert.True(Find(paragraphs, "다음 쪽").PageBreakBefore);

        // Lists keep their number formats, start and level.
        var item = Find(paragraphs, "하위 항목").List!;
        Assert.Equal((1, "HANGUL_SYLLABLE", "^2)"), (item.Level, item.Numbering.Levels[1].Format, item.Numbering.Levels[1].Text));
        Assert.Equal(3, item.Numbering.Levels[0].Start);

        // Links, bookmarks and notes.
        var body = Find(paragraphs, "본문");
        Assert.Equal(["https://example.com/?q=1", "#intro"], body.Inlines.OfType<HLink>().Select(l => l.Target));
        Assert.Equal("intro", Find(paragraphs, "큰 제목").Inlines.OfType<HBookmark>().Single().Name);
        Assert.Equal("각주 설명", TextOf((HParagraph)body.Inlines.OfType<HNote>().Single().Blocks[0]).Trim());

        // Tables: merged cells, borders, shading, header row, alignment.
        var (tableA, tableB) = (original.Sections[0].Blocks.OfType<HTable>().Single(), first.Blocks.OfType<HTable>().Single());
        Assert.Equal(tableA.ColumnWidths, tableB.ColumnWidths);
        Assert.Equal(HAlign.Center, tableB.Align);
        Assert.True(tableB.Rows[0].Header);
        for (var r = 0; r < tableA.Rows.Count; r++)
        {
            Assert.Equal(tableA.Rows[r].Cells.Count, tableB.Rows[r].Cells.Count);
            for (var c = 0; c < tableA.Rows[r].Cells.Count; c++)
            {
                var (x, y) = (tableA.Rows[r].Cells[c], tableB.Rows[r].Cells[c]);
                Assert.Equal((x.Column, x.RowSpan, x.ColSpan, x.Fill, x.VerticalAlign), (y.Column, y.RowSpan, y.ColSpan, y.Fill, y.VerticalAlign));
                Assert.Equal(x.Borders?.Bottom.Style, y.Borders?.Bottom.Style);
                Assert.Equal(x.Borders?.Bottom.Color, y.Borders?.Bottom.Color);
            }
        }

        // Pictures (inline and floating) and the text box keep size and position.
        var objects = first.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).ToList();
        var pictures = objects.OfType<HImage>().ToList();
        Assert.Equal(2, pictures.Count);
        Assert.Null(pictures[0].Anchor);
        Assert.Equal((3600, 3600), (pictures[0].Width, pictures[0].Height));
        Assert.Equal(new HAnchor("PAPER", "LEFT", 7200, "PARA", "TOP", 1000, "SQUARE", true), pictures[1].Anchor);
        var box = objects.OfType<HTextBox>().Single();
        Assert.Equal((14400, 7200, "#FFF2CC", HVerticalAlign.Center), (box.Width, box.Height, box.Fill, box.VerticalAlign));
        Assert.Equal(("PAGE", "CENTER", "PAPER", 28346, "TOP_AND_BOTTOM"), (box.Anchor!.HorizontalRelativeTo, box.Anchor.HorizontalAlign, box.Anchor.VerticalRelativeTo, box.Anchor.VerticalOffset, box.Anchor.Wrap));
        Assert.Equal(("#C00000", HBorderStyle.Solid), (box.Line.Color, box.Line.Style));
        Assert.Equal("글상자 안의 글", TextOf((HParagraph)box.Blocks[0]));

        if (await ConvertibleByLibreOfficeAsync(fx, written) is { } pages)
            Assert.True(pages >= 3);
    }
}
