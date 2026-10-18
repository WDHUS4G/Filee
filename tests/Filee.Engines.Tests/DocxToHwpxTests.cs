// DOCX → HWPX with the built-in reader and writer: layout that Pandoc-based conversion loses (page setup,
// headers/footers, columns, sections, text boxes, shapes, floating pictures, formatting, table borders) must
// arrive in the OWPML structure 한글 uses. Every file is also checked for package validity and rendered by rhwp.

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxToHwpxTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private async Task<string> ConvertAsync(DocxBuilder docx, string name, int? expectImages = 0)
    {
        var path = docx.Save(Path.Combine(fx.NewFolder(), name + ".docx"));
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "hwpx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var hwpx = job.Outputs.Single();
        ValidPackage(hwpx, expectImages);
        await ReadableByH2OrestartAsync(fx, hwpx);
        return hwpx;
    }

    private static int Int(XElement element, string attribute) => (int)element.Attribute(attribute)!;

    [Fact]
    public async Task Page_setup_headers_footers_and_page_numbers()
    {
        var docx = new DocxBuilder();
        var header = docx.Header(P(R("머리말 제목"), "<w:jc w:val=\"right\"/>"));
        var footer = docx.Footer(P(R("- ") + Field("PAGE", "1") + R(" / ") + Field("NUMPAGES", "3") + R(" -"), "<w:jc w:val=\"center\"/>"));
        docx.Paragraph(P(R("가로 방향 본문")));
        docx.SectionProperties =
            $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/><w:footerReference w:type=\"default\" r:id=\"{footer}\"/>" +
            "<w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/>" +
            "<w:pgMar w:top=\"1985\" w:right=\"1134\" w:bottom=\"1418\" w:left=\"1134\" w:header=\"851\" w:footer=\"851\" w:gutter=\"0\"/><w:titlePg/>";

        var hwpx = await ConvertAsync(docx, "page-setup");
        var section = Xml(hwpx);

        // Landscape: portrait-sized sheet turned by "NARROWLY".
        var pagePr = section.Descendants(Hp + "pagePr").Single();
        Assert.Equal("NARROWLY", (string?)pagePr.Attribute("landscape"));
        Assert.Equal((11906 * 5, 16838 * 5), (Int(pagePr, "width"), Int(pagePr, "height")));
        // Word measures the body from the paper edge; 한글 stacks header area on top of the top margin.
        var margin = pagePr.Element(Hp + "margin")!;
        Assert.Equal(851 * 5, Int(margin, "top"));
        Assert.Equal((1985 - 851) * 5, Int(margin, "header"));
        Assert.Equal(851 * 5, Int(margin, "bottom"));
        Assert.Equal((1418 - 851) * 5, Int(margin, "footer"));
        Assert.Equal(1134 * 5, Int(margin, "left"));

        var headerControl = section.Descendants(Hp + "header").Single();
        Assert.Equal("BOTH", (string?)headerControl.Attribute("applyPageType"));
        Assert.Contains("머리말 제목", headerControl.Value);
        var footerControl = section.Descendants(Hp + "footer").Single();
        var numbers = footerControl.Descendants(Hp + "autoNum").Select(a => (string?)a.Attribute("numType")).ToList();
        Assert.Equal(["PAGE", "TOTAL_PAGE"], numbers);
        Assert.DoesNotContain("3", string.Concat(footerControl.Descendants(Hp + "t").Select(t => t.Value))); // cached field results are dropped

        // "Different first page" without a first-page header hides header and footer on page 1.
        var visibility = section.Descendants(Hp + "visibility").Single();
        Assert.Equal(("1", "1"), ((string?)visibility.Attribute("hideFirstHeader"), (string?)visibility.Attribute("hideFirstFooter")));
        RenderWithRhwp(fx, hwpx, "docx-page-setup");
    }

    [Fact]
    public async Task Columns_change_within_a_section_and_new_paper_starts_a_new_section()
    {
        var docx = new DocxBuilder()
            .Paragraph(P(R("한 단 본문")))
            .Paragraph(P(R("첫 구역 끝"), $"<w:sectPr>{DefaultPage}</w:sectPr>"))
            .Paragraph(P(R("두 단 왼쪽")))
            .Paragraph(P(R("두 단 끝"), $"<w:sectPr><w:type w:val=\"continuous\"/>{DefaultPage}<w:cols w:num=\"2\" w:space=\"720\" w:sep=\"1\"/></w:sectPr>"))
            .Paragraph(P(R("레터 용지")));
        docx.SectionProperties = "<w:type w:val=\"nextPage\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>";

        var hwpx = await ConvertAsync(docx, "sections");

        var first = Xml(hwpx);
        var columnChange = ParagraphWith(first, "두 단 왼쪽").Descendants(Hp + "colPr").Single();
        Assert.Equal((2, 3600), (Int(columnChange, "colCount"), Int(columnChange, "sameGap")));
        Assert.NotNull(columnChange.Element(Hp + "colLine"));
        Assert.Equal(1, Int(first.Descendants(Hp + "colPr").First(), "colCount"));

        var second = Xml(hwpx, "Contents/section1.xml");
        var pagePr = second.Descendants(Hp + "pagePr").Single();
        Assert.Equal(("WIDELY", 61200, 79200), ((string)pagePr.Attribute("landscape")!, Int(pagePr, "width"), Int(pagePr, "height")));
        Assert.Contains("레터 용지", second.Value);
        Assert.Equal(2, RenderWithRhwp(fx, hwpx, "docx-sections") ?? 2);
    }

    [Fact]
    public async Task Paragraph_and_character_formatting_lists_and_document_grid()
    {
        var docx = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="1">
                  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="400" w:hanging="400"/></w:pPr></w:lvl>
                  <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="ganada"/><w:lvlText w:val="%2)"/><w:pPr><w:ind w:left="800" w:hanging="400"/></w:pPr></w:lvl>
                </w:abstractNum>
                <w:abstractNum w:abstractNumId="2"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/><w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol"/></w:rPr></w:lvl></w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                <w:num w:numId="2"><w:abstractNumId w:val="2"/></w:num>
                """,
            SectionProperties = DefaultPage + "<w:docGrid w:type=\"lines\" w:linePitch=\"360\"/>",
        };
        docx.Paragraph(P(R("가운데 제목"), "<w:pStyle w:val=\"Heading1\"/><w:jc w:val=\"center\"/>"))
            .Paragraph(P(R("굵은 빨강", "<w:b/><w:color w:val=\"FF0000\"/><w:sz w:val=\"28\"/><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"굴림\"/>") +
                         R(" 형광펜", "<w:highlight w:val=\"yellow\"/>") + R(" 무늬", "<w:shd w:val=\"pct25\" w:color=\"auto\" w:fill=\"auto\"/>"),
                "<w:ind w:left=\"720\" w:hanging=\"360\"/><w:spacing w:before=\"240\" w:after=\"120\"/>"))
            .Paragraph(P(R("첫 항목"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("하위 항목"), "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("글머리 항목"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr>"))
            .Paragraph(P(R("2") + R("016-06-15")));

        var hwpx = await ConvertAsync(docx, "formatting");
        var section = Xml(hwpx);
        var head = Xml(hwpx, "Contents/header.xml");
        XElement ParaPr(XElement paragraph) => head.Descendants(Hh + "paraPr").Single(p => (string?)p.Attribute("id") == (string?)paragraph.Attribute("paraPrIDRef"));
        XElement CharPr(XElement run) => head.Descendants(Hh + "charPr").Single(c => (string?)c.Attribute("id") == (string?)run.Attribute("charPrIDRef"));
        int Margin(XElement paraPr, string branch, string name) =>
            (int)paraPr.Descendants(Hp + branch).Single().Descendants(Hc + name).Single().Attribute("value")!;

        var title = ParagraphWith(section, "가운데 제목");
        var headingStyle = head.Descendants(Hh + "style").Single(s => (string?)s.Attribute("engName") == "Heading 1");
        Assert.Equal((string?)headingStyle.Attribute("id"), (string?)title.Attribute("styleIDRef"));
        Assert.Equal("CENTER", (string?)ParaPr(title).Element(Hh + "align")!.Attribute("horizontal"));

        var red = CharPr(RunWith(section, "굵은 빨강"));
        Assert.Equal(("1400", "#FF0000"), ((string)red.Attribute("height")!, (string)red.Attribute("textColor")!));
        Assert.NotNull(red.Element(Hh + "bold"));
        string Font(string language) =>
            (string)head.Descendants(Hh + "fontface").Single(f => (string?)f.Attribute("lang") == language)
                .Elements(Hh + "font").Single(f => (string?)f.Attribute("id") == (string?)red.Element(Hh + "fontRef")!.Attribute(language.ToLowerInvariant())).Attribute("face")!;
        Assert.Equal(("Arial", "굴림"), (Font("LATIN"), Font("HANGUL")));
        Assert.Equal("#FFFF00", (string?)CharPr(RunWith(section, "형광펜")).Attribute("shadeColor"));
        Assert.Equal("#BFBFBF", (string?)CharPr(RunWith(section, "무늬")).Attribute("shadeColor")); // 25% black on white

        // Hanging indent (Word: text at 0.5", first line at 0.25"). 한글 keeps a hanging paragraph's first line at
        // "left" and indents the rest, so left is the first-line position. hp:default repeats the values doubled.
        var indented = ParaPr(ParagraphWith(section, "굵은 빨강"));
        Assert.Equal((1800, -1800, 1200, 600), (Margin(indented, "case", "left"), Margin(indented, "case", "intent"), Margin(indented, "case", "prev"), Margin(indented, "case", "next")));
        Assert.Equal((3600, -3600), (Margin(indented, "default", "left"), Margin(indented, "default", "intent")));

        // Word's 18 pt document grid: 10 pt lines become 18 pt, 14 pt lines need two grid lines.
        var gridLine = ParaPr(ParagraphWith(section, "첫 항목")).Descendants(Hp + "case").Single().Element(Hh + "lineSpacing")!;
        Assert.Equal(("AT_LEAST", 1800), ((string)gridLine.Attribute("type")!, Int(gridLine, "value")));
        Assert.Equal(3600, Int(indented.Descendants(Hp + "case").Single().Element(Hh + "lineSpacing")!, "value"));

        // Lists: Word levels map to 한글 numbering levels; bullets use a symbol instead of a counter.
        var item = ParaPr(ParagraphWith(section, "첫 항목")).Element(Hh + "heading")!;
        var subItem = ParaPr(ParagraphWith(section, "하위 항목")).Element(Hh + "heading")!;
        Assert.Equal(("NUMBER", 0, 1), ((string)item.Attribute("type")!, Int(item, "level"), Int(subItem, "level")));
        var numbering = head.Descendants(Hh + "numbering").Single(n => (string?)n.Attribute("id") == (string?)item.Attribute("idRef"));
        var levels = numbering.Elements(Hh + "paraHead").Take(2).Select(h => ((string?)h.Attribute("numFormat"), h.Value)).ToList();
        Assert.Equal([("DIGIT", "^1."), ("HANGUL_SYLLABLE", "^2)")], levels);
        var bullet = ParaPr(ParagraphWith(section, "글머리 항목")).Element(Hh + "heading")!;
        Assert.Equal("●", head.Descendants(Hh + "numbering").Single(n => (string?)n.Attribute("id") == (string?)bullet.Attribute("idRef")).Elements(Hh + "paraHead").First().Value);

        // Runs split by Word but formatted alike become one text run.
        Assert.Contains(section.Descendants(Hp + "t"), t => t.Value == "2016-06-15");
        RenderWithRhwp(fx, hwpx, "docx-formatting");
    }

    [Fact]
    public async Task Tables_keep_merged_cells_borders_and_shading()
    {
        var cell = (string content, string tcPr) => $"<w:tc><w:tcPr>{tcPr}</w:tcPr>{P(R(content))}</w:tc>";
        var docx = new DocxBuilder().Paragraph(
            "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:jc w:val=\"center\"/></w:tblPr>" +
            "<w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
            "<w:tr><w:trPr><w:tblHeader/></w:trPr>" + cell("가로 병합", "<w:gridSpan w:val=\"2\"/><w:shd w:val=\"pct10\" w:color=\"auto\" w:fill=\"auto\"/>") +
                cell("세로 병합", "<w:vMerge w:val=\"restart\"/><w:vAlign w:val=\"center\"/>") + "</w:tr>" +
            "<w:tr>" + cell("A", "") + cell("B", "") + cell("", "<w:vMerge/>") + "</w:tr>" +
            "<w:tr>" + cell("C", "<w:tcBorders><w:bottom w:val=\"double\" w:sz=\"12\" w:color=\"FF0000\"/></w:tcBorders>") + cell("D", "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FFF2CC\"/>") + cell("E", "") + "</w:tr>" +
            "</w:tbl>" + P(""));

        var hwpx = await ConvertAsync(docx, "table");
        var section = Xml(hwpx);
        var table = section.Descendants(Hp + "tbl").Single();
        Assert.Equal((3, 3, "1"), (Int(table, "rowCnt"), Int(table, "colCnt"), (string?)table.Attribute("repeatHeader")));
        Assert.Equal("CENTER", (string?)table.Element(Hp + "pos")!.Attribute("horzAlign"));

        var cells = table.Descendants(Hp + "tc").Select(tc => new
        {
            Text = tc.Element(Hp + "subList")!.Value,
            Row = Int(tc.Element(Hp + "cellAddr")!, "rowAddr"),
            Column = Int(tc.Element(Hp + "cellAddr")!, "colAddr"),
            RowSpan = Int(tc.Element(Hp + "cellSpan")!, "rowSpan"),
            ColSpan = Int(tc.Element(Hp + "cellSpan")!, "colSpan"),
            Width = Int(tc.Element(Hp + "cellSz")!, "width"),
            BorderFill = HeadItem(hwpx, "borderFill", (string)tc.Attribute("borderFillIDRef")!),
            VerticalAlign = (string?)tc.Element(Hp + "subList")!.Attribute("vertAlign"),
        }).ToList();
        // Every grid position is covered exactly once.
        var covered = cells.SelectMany(c => Enumerable.Range(c.Row, c.RowSpan).SelectMany(r => Enumerable.Range(c.Column, c.ColSpan).Select(col => (r, col)))).ToList();
        Assert.Equal(9, covered.Distinct().Count());
        Assert.Equal(9, covered.Count);

        var wide = cells.Single(c => c.Text == "가로 병합");
        Assert.Equal((2, 25000), (wide.ColSpan, wide.Width));
        Assert.Equal("#E6E6E6", (string?)wide.BorderFill.Descendants(Hc + "winBrush").Single().Attribute("faceColor"));
        var tall = cells.Single(c => c.Text == "세로 병합");
        Assert.Equal((2, "CENTER"), (tall.RowSpan, tall.VerticalAlign));
        var bottom = cells.Single(c => c.Text == "C").BorderFill.Element(Hh + "bottomBorder")!;
        Assert.Equal(("DOUBLE_SLIM", "#FF0000"), ((string)bottom.Attribute("type")!, (string)bottom.Attribute("color")!));
        Assert.Equal("SOLID", (string?)cells.Single(c => c.Text == "A").BorderFill.Element(Hh + "leftBorder")!.Attribute("type"));
        Assert.Equal("#FFF2CC", (string?)cells.Single(c => c.Text == "D").BorderFill.Descendants(Hc + "winBrush").Single().Attribute("faceColor"));
        RenderWithRhwp(fx, hwpx, "docx-table");
    }

    [Fact]
    public async Task Text_boxes_shapes_and_floating_pictures()
    {
        var docx = new DocxBuilder();
        using var image = new MagickImage(MagickColors.MediumPurple, 120, 60);
        var picture = docx.Image(image.ToByteArray(MagickFormat.Png));
        const string Column = "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>";
        const string Paragraph = "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>";
        string Shape(string geometry, string fill, string line, long cx, long cy, string text = "") =>
            $"<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"{geometry}\"/>{fill}{line}</wps:spPr>" +
            (text.Length > 0 ? $"<wps:txbx><w:txbxContent>{P(R(text))}</w:txbxContent></wps:txbx>" : "") + "<wps:bodyPr anchor=\"ctr\"/></wps:wsp>";

        docx.Paragraph(P(Anchor(PictureUri, Picture(picture, 914400, 457200), 914400, 457200,
                "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>", Paragraph) + R("그림 옆에 흐르는 글")))
            .Paragraph(P(InlinePicture(picture, 457200, 457200)))
            .Paragraph(P(Anchor(PictureUri, Picture(picture, 457200, 457200), 457200, 457200,
                "<wp:positionH relativeFrom=\"margin\"><wp:posOffset>-635000</wp:posOffset></wp:positionH>",
                "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>-127000</wp:posOffset></wp:positionV>") + R("여백으로 나간 그림")))
            .Paragraph(P(Anchor(ShapeUri,
                Shape("rect", "<a:solidFill><a:srgbClr val=\"FFF2CC\"/></a:solidFill>", "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln>", 1828800, 914400, "글상자 안의 글"),
                1828800, 914400, "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>",
                "<wp:positionV relativeFrom=\"page\"><wp:posOffset>3600000</wp:posOffset></wp:positionV>", "<wp:wrapTopAndBottom/>")))
            .Paragraph(P(Anchor(ShapeUri, Shape("line", "", "<a:ln w=\"25400\"><a:solidFill><a:srgbClr val=\"4F81BD\"/></a:solidFill></a:ln>", 3000000, 0),
                3000000, 0, Column, Paragraph, "<wp:wrapNone/>")))
            .Paragraph(P(Anchor(ShapeUri,
                Shape("rect", "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"99CCFF\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"FFFFFF\"/></a:gs></a:gsLst><a:lin ang=\"0\"/></a:gradFill>", "<a:ln><a:noFill/></a:ln>", 2000000, 180000),
                2000000, 180000, Column, Paragraph, "<wp:wrapNone/>")));

        var hwpx = await ConvertAsync(docx, "objects", expectImages: 1); // one picture used twice is stored once
        var section = Xml(hwpx);

        var pictures = section.Descendants(Hp + "pic").ToList();
        var floating = pictures.Single(p => (string?)p.Element(Hp + "pos")!.Attribute("treatAsChar") == "0" && Int(p.Element(Hp + "pos")!, "horzOffset") == 7200);
        Assert.Equal(("SQUARE", "PAPER", 7200), ((string)floating.Attribute("textWrap")!, (string)floating.Element(Hp + "pos")!.Attribute("horzRelTo")!, Int(floating.Element(Hp + "pos")!, "horzOffset")));
        Assert.Single(pictures, p => (string?)p.Element(Hp + "pos")!.Attribute("treatAsChar") == "1");
        // Negative offsets are not allowed in 한글 files: into the margin → measured from the paper edge instead.
        var outside = pictures.Single(p => (string?)p.Element(Hp + "pos")!.Attribute("horzRelTo") == "PAPER" && Int(p.Element(Hp + "pos")!, "horzOffset") != 7200).Element(Hp + "pos")!;
        Assert.Equal((1440 * 5 - 5000, 0), (Int(outside, "horzOffset"), Int(outside, "vertOffset")));
        Assert.DoesNotContain(section.Descendants(Hp + "pos"), p => Int(p, "horzOffset") < 0 || Int(p, "vertOffset") < 0);

        var textBox = section.Descendants(Hp + "rect").Single(r => r.Element(Hp + "drawText") is not null);
        Assert.Contains("글상자 안의 글", textBox.Element(Hp + "drawText")!.Value);
        Assert.Equal("CENTER", (string?)textBox.Descendants(Hp + "subList").First().Attribute("vertAlign"));
        Assert.Equal("#FFF2CC", (string?)textBox.Descendants(Hc + "winBrush").Single().Attribute("faceColor"));
        Assert.Equal(("#C00000", 100), ((string)textBox.Element(Hp + "lineShape")!.Attribute("color")!, Int(textBox.Element(Hp + "lineShape")!, "width")));
        var position = textBox.Element(Hp + "pos")!;
        Assert.Equal(("PAGE", "CENTER", "PAPER", 28346), ((string)position.Attribute("horzRelTo")!, (string)position.Attribute("horzAlign")!, (string)position.Attribute("vertRelTo")!, Int(position, "vertOffset")));
        Assert.Equal("TOP_AND_BOTTOM", (string?)textBox.Attribute("textWrap"));

        var line = section.Descendants(Hp + "line").Single();
        Assert.Equal(23622, Int(line.Element(Hc + "endPt")!, "x"));
        Assert.Equal("#4F81BD", (string?)line.Element(Hp + "lineShape")!.Attribute("color"));
        var gradient = section.Descendants(Hp + "rect").Single(r => r.Element(Hp + "drawText") is null).Descendants(Hc + "gradation").Single();
        Assert.Equal(["#99CCFF", "#FFFFFF"], gradient.Elements(Hc + "color").Select(c => (string)c.Attribute("value")!));
        RenderWithRhwp(fx, hwpx, "docx-objects");
    }

    [Fact]
    public async Task Notes_links_bookmarks_and_page_breaks()
    {
        var docx = new DocxBuilder();
        docx.Footnotes("<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r>" + R(" 각주 설명") + "</w:p></w:footnote>");
        var url = docx.Hyperlink("https://example.com/a?b=1");
        docx.Paragraph(P("<w:bookmarkStart w:id=\"0\" w:name=\"intro\"/>" + R("소개") + "<w:bookmarkEnd w:id=\"0\"/>", "<w:pStyle w:val=\"Heading1\"/>"))
            .Paragraph(P(R("본문") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>" +
                         $"<w:hyperlink r:id=\"{url}\">" + R("웹 링크", "<w:rStyle w:val=\"Hyperlink\"/>") + "</w:hyperlink>" +
                         "<w:hyperlink w:anchor=\"intro\">" + R("소개로 이동") + "</w:hyperlink>"))
            .Paragraph(P(R("앞 쪽") + "<w:r><w:br w:type=\"page\"/></w:r>" + R("다음 쪽")));

        var hwpx = await ConvertAsync(docx, "notes-links");
        var section = Xml(hwpx);

        // The note number is an autoNum control at the start of the note's first paragraph.
        var note = section.Descendants(Hp + "footNote").Single();
        var firstRun = note.Descendants(Hp + "p").First().Elements(Hp + "run").First();
        Assert.Equal("FOOTNOTE", (string?)firstRun.Descendants(Hp + "autoNum").Single().Attribute("numType"));
        Assert.Contains("각주 설명", note.Value);

        string Parameter(XElement field, string name) => field.Descendants().Single(p => (string?)p.Attribute("name") == name).Value;
        var links = section.Descendants(Hp + "fieldBegin").ToList();
        var web = links.Single(l => Parameter(l, "Category") == "HWPHYPERLINK_TYPE_URL");
        Assert.Equal("https\\://example.com/a\\?b=1;1;0;0;", Parameter(web, "Command"));
        Assert.Equal("https://example.com/a?b=1", Parameter(web, "Path"));
        var bookmarkLink = links.Single(l => Parameter(l, "Category") == "HWPHYPERLINK_TYPE_HWP");
        Assert.Equal("?intro;0;0;0;", Parameter(bookmarkLink, "Command"));
        Assert.Equal("intro", (string?)section.Descendants(Hp + "bookmark").Single().Attribute("name"));
        Assert.Equal(2, section.Descendants(Hp + "fieldEnd").Count());

        Assert.Equal("1", (string?)ParagraphWith(section, "다음 쪽").Attribute("pageBreak"));
        Assert.Equal("0", (string?)ParagraphWith(section, "앞 쪽").Attribute("pageBreak"));
        Assert.Equal(2, RenderWithRhwp(fx, hwpx, "docx-notes-links") ?? 2);
    }
}
