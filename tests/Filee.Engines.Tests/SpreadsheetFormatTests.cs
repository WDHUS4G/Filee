// XLS, ODS and TSV without Excel or LibreOffice: XLS and ODS are read in-process with what the cells show, values
// and number formats, XLSX / XLS / ODS / CSV / TSV convert to each other keeping real numbers, and XLS / ODS / TSV
// become HWPX and PDF. Inputs come from XlsBuilder, OdsBuilder and XlsxBuilder; LibreOffice, when installed,
// cross-checks the ODS we write and the ODS / XLS it writes.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public class SpreadsheetFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";

    /// <summary>1 October 2026 as an Excel serial date.</summary>
    private const double October1 = 46296;

    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice"];

    private RoutePlanner BuiltInPlanner(params string[] assumeInstalled) =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }
            .CreatePlanner(assumeInstalled);

    private async Task<IReadOnlyList<string>> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return [.. job.Outputs];
    }

    /// <summary>
    /// "매출": centred title merged over four columns, number / date / percent / time formats, a boolean, an error,
    /// custom widths, a hidden row and column, a taller row; "비밀" is hidden; "메모" holds a line break.
    /// </summary>
    /// <param name="filler">Rows of numbers added to "메모": files under 4 KB keep the workbook in the compound
    /// file's mini stream, bigger ones in regular sectors.</param>
    private static string SalesXls(string folder, int filler = 0)
    {
        var xls = new XlsBuilder();
        xls.Sheet("매출")
            .Text(0, 0, "지점별 매출", XlsBuilder.Centered).Merge(0, 0, 0, 3)
            .Text(1, 0, "지점").Text(1, 1, "매출").Text(1, 2, "일자").Text(1, 3, "성장률")
            .Text(2, 0, "서울 강남").Number(2, 1, 1350000, XlsBuilder.Won).Number(2, 2, October1, XlsBuilder.Date).Number(2, 3, 0.128, XlsBuilder.Percent)
            .Text(2, 4, "숨긴 열")
            .Text(3, 0, "부산 서면").Number(3, 1, 870000, XlsBuilder.Won).Number(3, 2, October1 + 1, XlsBuilder.Date).Number(3, 3, 0.09, XlsBuilder.Percent)
            .Text(4, 0, "합계").Number(4, 1, 2220000, XlsBuilder.Won).Bool(4, 2, true).Error(4, 3, 0x07)
            .Text(5, 0, "숨긴 행").Height(5, 15, hide: true)
            .Number(6, 0, 0.5, XlsBuilder.Time).Number(6, 1, 3.5).Text(6, 2, "오른쪽 위", XlsBuilder.RightTop).Height(6, 30)
            .Width(0, 14).Width(1, 12).Width(2, 12).Width(3, 12).Width(4, 20, hide: true);
        xls.Sheet("비밀", hidden: true).Text(0, 0, "보이면 안 됨");
        var memo = xls.Sheet("메모").Text(0, 0, "첫 줄\n둘째 줄").Number(1, 0, 42);
        for (var i = 0; i < filler; i++)
            memo.Number(2 + i, 0, i * 1.5);
        return xls.Save(Path.Combine(folder, "매출보고.xls"));
    }

    // ───────────────────────── XLS ─────────────────────────

    [Fact]
    public void Xls_values_formats_and_layout_are_read_like_excel_shows_them()
    {
        var book = XlsReader.Read(SalesXls(fx.NewFolder()));

        Assert.Equal(["매출", "메모"], book.Sheets.Select(s => s.Name)); // the hidden sheet is left out
        var sales = book.Sheets[0];
        var won = sales.Get(2, 1)!;
        Assert.Equal(("1,350,000원", true, 1350000.0, "#,##0\"원\""), (won.Text, won.IsNumber, won.Value, won.NumberFormat));
        var date = sales.Get(2, 2)!;
        Assert.Equal(("2026-10-01", October1, "yyyy-mm-dd"), (date.Text, date.Value, date.NumberFormat));
        Assert.Equal(("12.8%", "0.0%"), (sales.Get(2, 3)!.Text, sales.Get(2, 3)!.NumberFormat));
        Assert.Equal("2,220,000원", sales.Get(4, 1)!.Text);
        Assert.Equal(("TRUE", true, 1.0), (sales.Get(4, 2)!.Text, sales.Get(4, 2)!.IsBoolean, sales.Get(4, 2)!.Value));
        Assert.Equal(("#DIV/0!", false), (sales.Get(4, 3)!.Text, sales.Get(4, 3)!.IsNumber));
        var time = sales.Get(6, 0)!;
        Assert.Equal(("12:00", 0.5, "h:mm"), (time.Text, time.Value, time.NumberFormat));
        Assert.Equal(("3.5", null), (sales.Get(6, 1)!.Text, sales.Get(6, 1)!.NumberFormat)); // General

        Assert.Equal(CellAlign.Center, sales.Get(0, 0)!.Style.Align);
        Assert.Equal((CellAlign.Right, CellVerticalAlign.Top), (sales.Get(6, 2)!.Style.Align, sales.Get(6, 2)!.Style.VerticalAlign));
        Assert.Equal(new CellRange(0, 0, 0, 3), Assert.Single(sales.Merges));
        Assert.Equal([14.0, 12.0, 12.0, 12.0], Enumerable.Range(0, 4).Select(sales.ColumnWidth));
        Assert.Equal([4], sales.HiddenColumns);
        Assert.Equal([5], sales.HiddenRows);
        Assert.Equal(30, Assert.Single(sales.RowHeights).Value); // only the row that differs from the usual height

        var memo = book.Sheets[1];
        Assert.Equal("첫 줄\n둘째 줄", memo.Get(0, 0)!.Text);
        Assert.Equal("42", memo.Get(1, 0)!.Text);
    }

    [Fact]
    public async Task Password_protected_xls_fails_with_a_clear_message()
    {
        var xls = new XlsBuilder();
        xls.Sheet("Sheet1").Text(0, 0, "secret");
        var path = xls.Save(Path.Combine(fx.NewFolder(), "잠금.xls"), encrypted: true);

        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => XlsReader.Read(path)).Message);
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "xlsx" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("password", Assert.Single(job.Files).ErrorDetail);
    }

    [Fact]
    public void Xlsx_saved_with_an_xls_name_is_read_as_xlsx()
    {
        var dir = fx.NewFolder();
        var xls = Path.Combine(dir, "웹에서 받은 파일.xls");
        File.Copy(OfficeTests.SalesWorkbook(dir), xls);

        var sales = XlsReader.Read(xls).Sheets[0];

        Assert.Equal("1,350,000원", sales.Get(2, 1)!.Text);
        Assert.Equal("#1F4E79", sales.Get(1, 0)!.Style.Fill); // what the XLSX reader keeps and ExcelDataReader doesn't
    }

    [Fact]
    public async Task Xls_to_xlsx_keeps_numbers_dates_and_their_formats()
    {
        var dir = fx.NewFolder();
        var xls = SalesXls(dir, filler: 400);
        Assert.True(new FileInfo(xls).Length > 8192);
        var source = XlsReader.Read(xls);

        var xlsx = Assert.Single(await ConvertAsync(xls, "xlsx"));

        using (var zip = ZipFile.OpenRead(xlsx))
        {
            XElement Part(string name)
            {
                using var stream = zip.GetEntry(name)!.Open();
                return XElement.Load(stream);
            }
            Assert.Equal(["매출", "메모"], Part("xl/workbook.xml").Descendants(S + "sheet").Select(s => (string)s.Attribute("name")!));
            var cells = Part("xl/worksheets/sheet1.xml").Descendants(S + "c").ToDictionary(c => (string)c.Attribute("r")!);
            var styles = Part("xl/styles.xml");
            var xfs = styles.Element(S + "cellXfs")!.Elements(S + "xf").ToList();
            string FormatCode(string reference)
            {
                var id = (int)xfs[(int)cells[reference].Attribute("s")!].Attribute("numFmtId")!;
                return (string)styles.Descendants(S + "numFmt").Single(f => (int)f.Attribute("numFmtId")! == id).Attribute("formatCode")!;
            }
            // Real numbers with their formats, not text: Excel can calculate with them.
            Assert.Null(cells["B3"].Attribute("t"));
            Assert.Equal("1350000", (string)cells["B3"].Element(S + "v")!);
            Assert.Equal("#,##0\"원\"", FormatCode("B3"));
            Assert.Equal(("46296", "yyyy-mm-dd"), ((string)cells["C3"].Element(S + "v")!, FormatCode("C3")));
            Assert.Equal("b", (string?)cells["C5"].Attribute("t"));
        }

        var result = XlsxReader.Read(xlsx);
        AssertSameCells(source, result);
        Assert.Equal(source.Sheets[0].Merges, result.Sheets[0].Merges);
        Assert.Equal(source.Sheets[0].HiddenRows, result.Sheets[0].HiddenRows);
        Assert.Equal(14, result.Sheets[0].ColumnWidth(0));
    }

    // ───────────────────────── ODS ─────────────────────────

    private const string OdsStyles =
        """
        <style:default-style style:family="table-cell"><style:text-properties fo:font-size="10pt"/></style:default-style>
        <style:style style:name="Default" style:family="table-cell"/>
        <style:style style:name="Heading" style:family="table-cell" style:parent-style-name="Default"><style:text-properties fo:font-weight="bold" fo:font-size="14pt"/></style:style>
        <number:number-style style:name="N4"><number:number number:decimal-places="2" number:min-decimal-places="2" number:min-integer-digits="1" number:grouping="true"/></number:number-style>
        """;

    private const string OdsAutomaticStyles =
        """
        <style:style style:name="co1" style:family="table-column"><style:table-column-properties style:column-width="2.54cm"/></style:style>
        <style:style style:name="co2" style:family="table-column"><style:table-column-properties style:column-width="0.8in"/></style:style>
        <style:style style:name="ro1" style:family="table-row"><style:table-row-properties style:row-height="0.178in" style:use-optimal-row-height="true"/></style:style>
        <style:style style:name="ro2" style:family="table-row"><style:table-row-properties style:row-height="30pt" style:use-optimal-row-height="false"/></style:style>
        <style:style style:name="ta1" style:family="table"><style:table-properties table:display="true"/></style:style>
        <style:style style:name="ta2" style:family="table"><style:table-properties table:display="false"/></style:style>
        <number:percentage-style style:name="N10"><number:number number:decimal-places="1" number:min-decimal-places="1" number:min-integer-digits="1"/><number:text>%</number:text></number:percentage-style>
        <number:currency-style style:name="N11P0" style:volatile="true"><number:currency-symbol>₩</number:currency-symbol><number:number number:decimal-places="0" number:min-integer-digits="1" number:grouping="true"/></number:currency-style>
        <number:currency-style style:name="N11"><style:text-properties fo:color="#ff0000"/><number:text>-</number:text><number:currency-symbol>₩</number:currency-symbol><number:number number:decimal-places="0" number:min-integer-digits="1" number:grouping="true"/><style:map style:condition="value()&gt;=0" style:apply-style-name="N11P0"/></number:currency-style>
        <number:date-style style:name="N12"><number:year number:style="long"/><number:text>년 </number:text><number:month/><number:text>월 </number:text><number:day/><number:text>일</number:text></number:date-style>
        <number:time-style style:name="N13"><number:hours number:style="long"/><number:text>:</number:text><number:minutes number:style="long"/></number:time-style>
        <style:style style:name="ce1" style:family="table-cell" style:parent-style-name="Heading"><style:table-cell-properties fo:background-color="#1f4e79" fo:border="0.75pt solid #000000" style:text-align-source="fix" style:vertical-align="middle"/><style:paragraph-properties fo:text-align="center"/><style:text-properties fo:color="#ffffff"/></style:style>
        <style:style style:name="ce2" style:family="table-cell" style:parent-style-name="Default" style:data-style-name="N4"/>
        <style:style style:name="ce3" style:family="table-cell" style:parent-style-name="Default" style:data-style-name="N10"/>
        <style:style style:name="ce4" style:family="table-cell" style:parent-style-name="Default" style:data-style-name="N11"/>
        <style:style style:name="ce5" style:family="table-cell" style:parent-style-name="Default" style:data-style-name="N12"/>
        <style:style style:name="ce6" style:family="table-cell" style:parent-style-name="Default" style:data-style-name="N13"/>
        <style:style style:name="ce7" style:family="table-cell" style:parent-style-name="Default"><style:table-cell-properties fo:border-bottom="2.5pt double #c00000" fo:wrap-option="wrap"/><style:text-properties fo:font-style="italic" style:text-underline-style="solid" style:text-line-through-style="solid"/></style:style>
        <style:style style:name="ce8" style:family="table-cell" style:parent-style-name="Default"><style:table-cell-properties fo:background-color="#ffff00"/></style:style>
        """;

    /// <summary>
    /// Laid out like LibreOffice saves sheets: repeated columns and rows up to the end of the sheet, a merged
    /// header row, every value type, text with spaces, tabs, line breaks and a comment, a hidden row, column and
    /// sheet, and a sheet whose formatting reaches the last row.
    /// </summary>
    private const string OdsTables =
        """
        <table:table table:name="매출" table:style-name="ta1">
          <table:table-column table:style-name="co1" table:default-cell-style-name="Default"/>
          <table:table-column table:style-name="co2" table:number-columns-repeated="2" table:default-cell-style-name="Default"/>
          <table:table-column table:style-name="co2" table:visibility="collapse" table:default-cell-style-name="Default"/>
          <table:table-column table:style-name="co1" table:number-columns-repeated="16380" table:default-cell-style-name="Default"/>
          <table:table-header-rows>
            <table:table-row table:style-name="ro2">
              <table:table-cell table:style-name="ce1" office:value-type="string" table:number-columns-spanned="3" table:number-rows-spanned="1"><text:p>지점별 매출</text:p></table:table-cell>
              <table:covered-table-cell table:number-columns-repeated="2"/>
              <table:table-cell table:number-columns-repeated="16381"/>
            </table:table-row>
          </table:table-header-rows>
          <table:table-row table:style-name="ro1">
            <table:table-cell table:style-name="ce2" office:value-type="float" office:value="1234.5"><text:p>1,234.50</text:p></table:table-cell>
            <table:table-cell table:style-name="ce3" office:value-type="percentage" office:value="0.128"><text:p>12.8%</text:p></table:table-cell>
            <table:table-cell table:style-name="ce4" office:value-type="currency" office:currency="KRW" office:value="-5000"><text:p>-₩5,000</text:p></table:table-cell>
            <table:table-cell table:number-columns-repeated="16381"/>
          </table:table-row>
          <table:table-row table:style-name="ro1">
            <table:table-cell table:style-name="ce5" office:value-type="date" office:date-value="2026-10-01"><text:p>2026년 10월 1일</text:p></table:table-cell>
            <table:table-cell table:style-name="ce6" office:value-type="time" office:time-value="PT13H30M00S"><text:p>13:30</text:p></table:table-cell>
            <table:table-cell office:value-type="boolean" office:boolean-value="true"><text:p>TRUE</text:p></table:table-cell>
            <table:table-cell office:value-type="string" calcext:value-type="error"><text:p>#DIV/0!</text:p></table:table-cell>
            <table:table-cell table:number-columns-repeated="16380"/>
          </table:table-row>
          <table:table-row table:style-name="ro1" table:number-rows-repeated="2">
            <table:table-cell office:value-type="float" office:value="7" table:number-columns-repeated="3"><text:p>7</text:p></table:table-cell>
            <table:table-cell table:number-columns-repeated="16381"/>
          </table:table-row>
          <table:table-row table:style-name="ro1" table:visibility="collapse">
            <table:table-cell office:value-type="string"><text:p>숨긴 행</text:p></table:table-cell>
            <table:table-cell table:number-columns-repeated="16383"/>
          </table:table-row>
          <table:table-row table:style-name="ro1">
            <table:table-cell table:style-name="ce7" office:value-type="string"><office:annotation><text:p>메모는 셀 내용이 아님</text:p></office:annotation><text:p><text:s/>앞 공백<text:s text:c="2"/>세 칸<text:tab/>탭<text:line-break/>줄</text:p><text:p><text:span>둘째</text:span> <text:span>문단</text:span></text:p></table:table-cell>
            <table:table-cell table:number-columns-repeated="16383"/>
          </table:table-row>
          <table:table-row table:style-name="ro1" table:number-rows-repeated="1048569">
            <table:table-cell table:number-columns-repeated="16384"/>
          </table:table-row>
        </table:table>
        <table:table table:name="비밀" table:style-name="ta2">
          <table:table-column/>
          <table:table-row><table:table-cell office:value-type="string"><text:p>보이면 안 됨</text:p></table:table-cell></table:table-row>
        </table:table>
        <table:table table:name="색칠" table:style-name="ta1">
          <table:table-column table:default-cell-style-name="ce8" table:number-columns-repeated="2"/>
          <table:table-column table:number-columns-repeated="16382"/>
          <table:table-row>
            <table:table-cell office:value-type="float" office:value="1"><text:p>1</text:p></table:table-cell>
            <table:table-cell/>
            <table:table-cell table:number-columns-repeated="16382"/>
          </table:table-row>
          <table:table-row table:number-rows-repeated="1048575">
            <table:table-cell table:style-name="ce8" table:number-columns-repeated="2"/>
            <table:table-cell table:number-columns-repeated="16382"/>
          </table:table-row>
        </table:table>
        """;

    private const string OdsSettings =
        $"""
        <office:document-settings {OdsBuilder.Namespaces} office:version="1.3"><office:settings>
          <config:config-item-set config:name="ooo:view-settings"><config:config-item-map-indexed config:name="Views"><config:config-item-map-entry>
            <config:config-item-map-named config:name="Tables">
              <config:config-item-map-entry config:name="색칠">
                <config:config-item config:name="VerticalSplitMode" config:type="short">2</config:config-item>
                <config:config-item config:name="VerticalSplitPosition" config:type="int">1</config:config-item>
              </config:config-item-map-entry>
            </config:config-item-map-named>
          </config:config-item-map-entry></config:config-item-map-indexed></config:config-item-set>
        </office:settings></office:document-settings>
        """;

    private string SalesOds(string folder) =>
        OdsBuilder.Save(Path.Combine(folder, "매출보고.ods"), OdsAutomaticStyles, OdsTables, OdsStyles, OdsSettings);

    [Fact]
    public void Ods_repeats_spans_values_and_styles_are_read_like_calc_shows_them()
    {
        var book = OdsReader.Read(SalesOds(fx.NewFolder()));

        Assert.Equal(["매출", "색칠"], book.Sheets.Select(s => s.Name)); // the hidden sheet is left out
        var sales = book.Sheets[0];
        Assert.Equal(6, sales.Rows.Keys.Max()); // the million empty rows at the end are not expanded
        Assert.Equal(3, sales.Rows.Values.Max(r => r.Keys.Max()));

        var title = sales.Get(0, 0)!;
        Assert.Equal("지점별 매출", title.Text);
        Assert.Equal((true, 14.0, "#FFFFFF", "#1F4E79"), (title.Style.Bold, title.Style.FontSize, title.Style.Color, title.Style.Fill));
        Assert.Equal((CellAlign.Center, CellVerticalAlign.Center), (title.Style.Align, title.Style.VerticalAlign));
        Assert.Equal(new CellBorder("thin", "#000000"), title.Style.Bottom);
        Assert.Equal(new CellRange(0, 0, 0, 2), Assert.Single(sales.Merges));

        void Is(int row, int column, string text, double? value, string? format)
        {
            var cell = sales.Get(row, column)!;
            Assert.Equal((text, value, format, value is not null), (cell.Text, cell.Value, cell.NumberFormat, cell.IsNumber));
        }
        Is(1, 0, "1,234.50", 1234.5, "#,##0.00");
        Is(1, 1, "12.8%", 0.128, "0.0%");
        Is(1, 2, "-₩5,000", -5000, "\"₩\"#,##0;[Red]-\"₩\"#,##0");
        Is(2, 0, "2026년 10월 1일", October1, "yyyy\"년 \"m\"월 \"d\"일\"");
        Is(2, 1, "13:30", 13.5 / 24, "hh:mm");
        Is(2, 3, "#DIV/0!", null, null);
        Assert.Equal(("TRUE", true, 1.0), (sales.Get(2, 2)!.Text, sales.Get(2, 2)!.IsBoolean, sales.Get(2, 2)!.Value));
        Assert.All(from r in new[] { 3, 4 } from c in new[] { 0, 1, 2 } select sales.Get(r, c), cell => Assert.Equal(7, cell!.Value));

        var text = sales.Get(6, 0)!;
        Assert.Equal(" 앞 공백  세 칸\t탭\n줄\n둘째 문단", text.Text); // the comment is not part of the text
        Assert.Equal((true, true, true, true, 10.0), (text.Style.Italic, text.Style.Underline, text.Style.Strike, text.Style.Wrap, text.Style.FontSize));
        Assert.Equal(new CellBorder("double", "#C00000"), text.Style.Bottom);

        Assert.Equal([5], sales.HiddenRows);
        Assert.Equal([3], sales.HiddenColumns);
        Assert.Equal(30, Assert.Single(sales.RowHeights).Value); // rows sized to their content have no custom height
        Assert.Equal([0, 1, 2, 3], sales.ColumnWidths.Keys.Order());
        Assert.Equal(72 / 5.25, sales.ColumnWidth(0), 3); // 2.54 cm
        Assert.Equal(57.6 / 5.25, sales.ColumnWidth(1), 3); // 0.8 in
        Assert.Equal(72 / 5.25, sales.DefaultColumnWidth, 3);
        Assert.Equal(1, sales.FrozenRows); // table:table-header-rows

        var painted = book.Sheets[1];
        Assert.Equal(1, painted.FrozenRows); // from settings.xml
        var row = Assert.Single(painted.Rows).Value; // the formatted rest of the sheet is not content
        Assert.Equal("#FFFF00", row[1].Style.Fill); // the column's default cell style
    }

    [Fact]
    public async Task Ods_to_csv_writes_what_calc_shows()
    {
        var outputs = await ConvertAsync(SalesOds(fx.NewFolder()), "csv");

        Assert.Equal(["매출보고_매출.csv", "매출보고_색칠.csv"], outputs.Select(Path.GetFileName));
        Assert.Equal(
            "지점별 매출,,\r\n" +
            "\"1,234.50\",12.8%,\"-₩5,000\"\r\n" +
            "2026년 10월 1일,13:30,TRUE\r\n" +
            "7,7,7\r\n" +
            "7,7,7\r\n" +
            "\" 앞 공백  세 칸\t탭\n줄\n둘째 문단\",,\r\n", // hidden row and column left out
            (await File.ReadAllTextAsync(outputs[0], TestContext.Current.CancellationToken)).TrimStart('﻿'));
    }

    [Fact]
    public async Task Password_protected_ods_fails_with_a_clear_message()
    {
        var path = OdsBuilder.Save(Path.Combine(fx.NewFolder(), "잠금.ods"), "", "<table:table table:name=\"A\"/>", encrypted: true);

        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => OdsReader.Read(path)).Message);
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "csv" });
        Assert.Contains("password", Assert.Single(job.Files).ErrorDetail);
    }

    [Fact]
    public async Task Xlsx_to_ods_and_back_keeps_values_formats_merges_and_widths()
    {
        var dir = fx.NewFolder();
        var xlsx = OfficeTests.SalesWorkbook(dir);
        var source = XlsxReader.Read(xlsx);

        var ods = Assert.Single(await ConvertAsync(xlsx, "ods"));

        using (var zip = ZipFile.OpenRead(ods))
        {
            // OpenDocument: "mimetype" first and stored, so the type can be read at a fixed offset.
            var mimetype = zip.Entries[0];
            Assert.Equal(("mimetype", mimetype.Length), (mimetype.FullName, mimetype.CompressedLength));
            using (var reader = new StreamReader(mimetype.Open()))
                Assert.Equal("application/vnd.oasis.opendocument.spreadsheet", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(zip.GetEntry("META-INF/manifest.xml"));

            XElement content;
            using (var stream = zip.GetEntry("content.xml")!.Open())
                content = XElement.Load(stream);
            var cells = content.Descendants(Table + "table-cell").ToList();
            // Numbers are numbers with a data style; text:p is the text Excel showed.
            var won = cells.Single(c => (string?)c.Attribute(Office + "value") == "1350000");
            Assert.Equal(("float", "1,350,000원"), ((string?)won.Attribute(Office + "value-type"), won.Element(Text + "p")!.Value));
            var dataStyle = content.Descendants().Single(e => (string?)e.Attribute(Style + "name") == (string?)content.Descendants(Style + "style")
                .Single(s => (string?)s.Attribute(Style + "name") == (string?)won.Attribute(Table + "style-name")).Attribute(Style + "data-style-name"));
            Assert.Equal("원", dataStyle.Element(Number + "text")!.Value);
            Assert.Contains(cells, c => (string?)c.Attribute(Office + "date-value") == "2026-10-01" && (string?)c.Attribute(Office + "value-type") == "date");
            Assert.Contains(cells, c => (string?)c.Attribute(Office + "value-type") == "percentage" && (string?)c.Attribute(Office + "value") == "0.128");
            Assert.Equal("4", (string?)cells.First(c => c.Value == "지점별 매출").Attribute(Table + "number-columns-spanned"));
            Assert.DoesNotContain("보이면 안 됨", content.Value);
        }

        var fromOds = OdsReader.Read(ods);
        AssertSameCells(source, fromOds);
        AssertSameLayout(source, fromOds);
        var header = fromOds.Sheets[0].Get(1, 0)!.Style;
        Assert.Equal((true, "#FFFFFF", "#1F4E79", CellAlign.Center, "thin"), (header.Bold, header.Color, header.Fill, header.Align, header.Bottom.Style));

        File.Move(ods, Path.Combine(dir, "사본.ods"));
        var back = Assert.Single(await ConvertAsync(Path.Combine(dir, "사본.ods"), "xlsx"));
        var result = XlsxReader.Read(back);
        AssertSameCells(source, result);
        AssertSameLayout(source, result);
        Assert.Equal("#1F4E79", result.Sheets[0].Get(1, 0)!.Style.Fill);
    }

    [Theory]
    [InlineData("0", "float")]
    [InlineData("0.00", "float")]
    [InlineData("#,##0", "float")]
    [InlineData("#,##0.00", "float")]
    [InlineData("0.0##", "float")]
    [InlineData("#,##0,", "float")]
    [InlineData("#,##0\"원\"", "float")]
    [InlineData("$#,##0.00", "float")]
    [InlineData("#,##0;[Red]-#,##0", "float")]
    [InlineData("#,##0.00;(#,##0.00);-", "float")]
    [InlineData("0%", "percentage")]
    [InlineData("0.0%", "percentage")]
    [InlineData("0.00E+00", "float")]
    [InlineData("yyyy-mm-dd", "date")]
    [InlineData("d-mmm-yy", "date")]
    [InlineData("yyyy-mm-dd h:mm", "date")]
    [InlineData("dddd, mmmm d", "date")]
    [InlineData("h:mm AM/PM", "time")]
    [InlineData("mm:ss", "time")]
    [InlineData("mmss.0", "time")]
    [InlineData("[h]:mm:ss", "time")]
    public void Excel_number_formats_survive_opendocument_data_styles(string code, string valueType)
    {
        var (xml, type) = OdsNumberStyles.ToOdf(code, "N1")!.Value;
        // Like OdsReader: a space alone in number:text is meaningful.
        var styles = XElement.Parse($"<styles {OdsBuilder.Namespaces}>{xml}</styles>", LoadOptions.PreserveWhitespace)
            .Elements().ToDictionary(e => (string)e.Attribute(Style + "name")!);

        Assert.Equal(valueType, type);
        Assert.Equal(code, OdsNumberStyles.ToExcel(styles["N1"], styles.GetValueOrDefault));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("General")]
    [InlineData("@")]
    [InlineData("[>=100]0;0.0")]
    [InlineData("# ?/?")]
    [InlineData("000-0000")]
    public void Excel_formats_without_a_data_style_stay_plain_numbers(string? code) =>
        Assert.Null(OdsNumberStyles.ToOdf(code, "N1"));

    private static void AssertSameCells(Workbook expected, Workbook actual)
    {
        Assert.Equal(expected.Sheets.Select(s => s.Name), actual.Sheets.Select(s => s.Name));
        foreach (var (want, got) in expected.Sheets.Zip(actual.Sheets))
        {
            foreach (var (row, cells) in want.Rows)
            {
                foreach (var (column, cell) in cells)
                {
                    var other = got.Get(row, column);
                    Assert.True(other is not null, $"{want.Name} R{row + 1}C{column + 1} is missing");
                    Assert.Equal((cell.Text, cell.IsNumber, cell.Value, cell.NumberFormat, cell.IsBoolean),
                        (other.Text, other.IsNumber, other.Value, other.NumberFormat, other.IsBoolean));
                }
            }
        }
    }

    private static void AssertSameLayout(Workbook expected, Workbook actual)
    {
        foreach (var (want, got) in expected.Sheets.Zip(actual.Sheets))
        {
            Assert.Equal(want.Merges, got.Merges);
            Assert.Equal(want.FrozenRows, got.FrozenRows);
            foreach (var column in want.ColumnWidths.Keys)
                Assert.Equal(want.ColumnWidth(column), got.ColumnWidth(column), 2);
        }
    }

    // ───────────────────────── TSV ─────────────────────────

    [Fact]
    public async Task Tsv_to_xlsx_and_back_keeps_every_value()
    {
        var dir = fx.NewFolder();
        // Commas are data in TSV: "1,200" and "Lee, J" stay one field each.
        const string Tsv = "이름\t금액\t메모\r\n김민지\t1,200\t\"줄\n바꿈\"\r\nLee, J\t3.5\t\"\"\"인용\"\"\"\r\n";
        var tsv = Path.Combine(dir, "명단.tsv");
        await File.WriteAllTextAsync(tsv, Tsv, TestContext.Current.CancellationToken);

        var xlsx = Assert.Single(await ConvertAsync(tsv, "xlsx"));
        var sheet = Assert.Single(XlsxReader.Read(xlsx).Sheets);
        Assert.Equal(("1,200", false), (sheet.Get(1, 1)!.Text, sheet.Get(1, 1)!.IsNumber));
        Assert.Equal(("Lee, J", 3.5), (sheet.Get(2, 0)!.Text, sheet.Get(2, 1)!.Value));
        Assert.Equal("줄\n바꿈", sheet.Get(1, 2)!.Text);

        File.Move(xlsx, Path.Combine(dir, "사본.xlsx"));
        var back = Assert.Single(await ConvertAsync(Path.Combine(dir, "사본.xlsx"), "tsv"));
        var bytes = await File.ReadAllBytesAsync(back, TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(Tsv, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        var csv = Assert.Single(await ConvertAsync(tsv, "csv"));
        Assert.StartsWith("이름,금액,메모\r\n김민지,\"1,200\",", (await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken)).TrimStart('﻿'));
    }

    [Fact]
    public async Task Workbook_to_tsv_writes_one_file_per_sheet()
    {
        var outputs = await ConvertAsync(OfficeTests.SalesWorkbook(fx.NewFolder()), "tsv");

        Assert.Equal(["매출보고_매출.tsv", "매출보고_메모.tsv", "매출보고_넓은 표.tsv"], outputs.Select(Path.GetFileName));
        Assert.StartsWith("지점별 매출\t\t\t\r\n지점\t매출\t일자\t성장률\r\n서울 강남\t1,350,000원\t2026-10-01\t12.8%\r\n",
            (await File.ReadAllTextAsync(outputs[0], TestContext.Current.CancellationToken)).TrimStart('﻿'));
    }

    // ───────────────────────── PDF ─────────────────────────

    [Theory]
    [InlineData("xls")]
    [InlineData("ods")]
    [InlineData("tsv")]
    public async Task Spreadsheets_to_pdf_without_libreoffice(string format)
    {
        Assert.SkipUnless(fx.Converters.Any(c => c.Id == "rhwp" && fx.Catalog.StatusOf(c).IsAvailable), "rhwp not found");
        var dir = fx.NewFolder();
        var input = format switch
        {
            "xls" => SalesXls(dir),
            "ods" => SalesOds(dir),
            _ => Path.Combine(dir, "표.tsv"),
        };
        if (format == "tsv")
            await File.WriteAllTextAsync(input, "항목\t값\r\n가\t1\r\n", TestContext.Current.CancellationToken);

        var pdf = Assert.Single(await ConvertAsync(input, "pdf"));

        // PDFium (for counting pages) is only known to the platform analyzer on desktop and mobile systems.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Assert.True(PDFtoImage.Conversion.GetPageCount(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken)) > 0);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("xls", "xlsx", "spreadsheet")]
    [InlineData("xls", "ods", "spreadsheet")]
    [InlineData("xls", "csv", "spreadsheet")]
    [InlineData("xls", "tsv", "spreadsheet")]
    [InlineData("ods", "xlsx", "spreadsheet")]
    [InlineData("ods", "csv", "spreadsheet")]
    [InlineData("xlsx", "ods", "spreadsheet")]
    [InlineData("xlsx", "tsv", "spreadsheet")]
    [InlineData("csv", "ods", "spreadsheet")]
    [InlineData("csv", "tsv", "spreadsheet")]
    [InlineData("tsv", "xlsx", "spreadsheet")]
    [InlineData("tsv", "ods", "spreadsheet")]
    [InlineData("xls", "hwpx", "hwpx-writer")]
    [InlineData("ods", "hwpx", "hwpx-writer")]
    [InlineData("tsv", "hwpx", "hwpx-writer")]
    [InlineData("xls", "pdf", "hwpx-writer,rhwp")]
    [InlineData("ods", "pdf", "hwpx-writer,rhwp")]
    [InlineData("tsv", "pdf", "hwpx-writer,rhwp")]
    public void Spreadsheets_convert_without_libreoffice(string from, string to, string engines)
    {
        Assert.Equal(engines.Split(','), BuiltInPlanner("rhwp").Plan(from, to)!.Steps.Select(s => s.Converter.Id));
        // Built-in conversions win even when LibreOffice is installed.
        Assert.DoesNotContain(fx.Catalog.CreatePlanner(["libreoffice", "rhwp"]).Plan(from, to)!.Steps, s => s.Converter.Id == "libreoffice");
    }

    [Fact]
    public void Spreadsheet_engine_has_no_same_format_edges()
    {
        var engine = fx.Converters.OfType<SpreadsheetConverter>().Single();
        Assert.DoesNotContain(engine.Edges, e => e.From == e.To);
        Assert.DoesNotContain(engine.Edges, e => e.To == "xls"); // XLS is read only; writing it needs LibreOffice
    }

    // ───────────────────────── LibreOffice cross-check ─────────────────────────

    [Fact]
    public async Task LibreOffice_reads_our_ods_and_we_read_its_ods_and_xls()
    {
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        Assert.SkipUnless(fx.Catalog.StatusOf(libreOffice).IsAvailable, "LibreOffice not installed");
        var dir = fx.NewFolder();
        var xlsx = OfficeTests.SalesWorkbook(dir);
        var ods = Assert.Single(await ConvertAsync(xlsx, "ods"));

        var books = new Dictionary<string, Workbook>
        {
            ["our ODS opened by LibreOffice"] = XlsxReader.Read(await LibreOfficeAsync(libreOffice, ods, "ods", "xlsx")),
            ["ODS by LibreOffice"] = OdsReader.Read(await LibreOfficeAsync(libreOffice, xlsx, "xlsx", "ods")),
            ["XLS by LibreOffice"] = XlsReader.Read(await LibreOfficeAsync(libreOffice, xlsx, "xlsx", "xls")),
        };
        foreach (var (name, book) in books)
        {
            var sales = book.Sheets[0];
            Assert.True(new[] { "매출", "메모", "넓은 표" }.SequenceEqual(book.Sheets.Select(s => s.Name)), name);
            Assert.True(sales.Get(2, 1)!.Text == "1,350,000원", $"{name}: {sales.Get(2, 1)!.Text}");
            Assert.True(sales.Get(2, 3)!.Text == "12.8%", $"{name}: {sales.Get(2, 3)!.Text}");
            // LibreOffice shows built-in dates in its own locale's order: compare the value.
            Assert.True(sales.Get(2, 2)!.Value == October1, $"{name}: {sales.Get(2, 2)!.Value}");
            Assert.True(sales.Get(4, 1)!.Text == "2,220,000원", $"{name}: {sales.Get(4, 1)!.Text}");
            Assert.True(new CellRange(0, 0, 0, 3) == Assert.Single(sales.Merges), name);
            Assert.True(sales.Get(1, 0)!.Style.Align == CellAlign.Center, name);
            // LibreOffice measures character widths with its own fonts, so widths through ODS move by up to a fifth;
            // XLS keeps Excel's own unit.
            var tolerance = name.StartsWith("XLS", StringComparison.Ordinal) ? 0.01 : 0.25;
            Assert.True(Math.Abs(sales.ColumnWidth(0) - 14) <= 14 * tolerance, $"{name}: {sales.ColumnWidth(0)}");
            Assert.True(Math.Abs(sales.ColumnWidth(0) / sales.ColumnWidth(1) - 14.0 / 12) < 0.02, $"{name}: {sales.ColumnWidth(0)} / {sales.ColumnWidth(1)}");
        }
    }

    private async Task<string> LibreOfficeAsync(IConverter libreOffice, string input, string from, string to)
    {
        var output = Path.Combine(Path.GetDirectoryName(input)!, $"{Path.GetFileNameWithoutExtension(input)}.lo-{from}.{to}");
        var written = await libreOffice.ConvertAsync(new ConversionStep(input, from, to, new Preset(), new FixedPath(output), fx.NewFolder()),
            null, TestContext.Current.CancellationToken);
        return written.Single();
    }

    private sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
