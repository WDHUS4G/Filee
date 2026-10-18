// Paragraphs, runs and their content: text with tabs and line breaks, fields (hyperlinks), bookmarks, notes, page
// numbers, section controls and tables. 한글 keeps tables inside runs like characters; in the model a table is a
// block of its own, so the paragraph around it is split.

using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxReader
{
    /// <summary>Fields (fieldBegin … fieldEnd) open in the current paragraph list; hyperlinks may span paragraphs.</summary>
    private List<OpenField> _fields = [];

    /// <summary>Highlighter colour between hp:markpenBegin and hp:markpenEnd.</summary>
    private string? _markpen;

    /// <summary>Inside tracked deleted text (hp:deleteBegin … hp:deleteEnd), which the finished document does not show.</summary>
    private bool _deleted;

    /// <summary>Numbering of outline paragraphs (개요) in the current section (secPr outlineShapeIDRef).</summary>
    private string? _outlineNumberingId;

    private sealed record OpenField(string? Id, string? Target);

    /// <summary>Paragraphs and tables of a paragraph list (cell, header, footer, note, text box, caption).</summary>
    private List<HBlock> ReadSubList(XElement? subList)
    {
        var blocks = new List<HBlock>();
        if (subList is null)
            return blocks;
        // Fields, highlighting and tracked deletions do not reach into or out of a nested paragraph list.
        var (fields, markpen, deleted) = (_fields, _markpen, _deleted);
        (_fields, _markpen, _deleted) = ([], null, false);
        try
        {
            foreach (var element in Elements(subList))
            {
                if (element.Name.LocalName == "p")
                    ReadParagraph(element, blocks, null);
                else
                    Skip(element);
            }
        }
        finally
        {
            (_fields, _markpen, _deleted) = (fields, markpen, deleted);
        }
        return blocks;
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>
    /// Builds the block(s) of one hp:p: a table inside it ends the paragraph so far, becomes a block, and the text
    /// after it continues in a new paragraph with the same format.
    /// </summary>
    private sealed class ParagraphBuilder
    {
        private readonly List<HBlock> _output;
        private readonly List<OpenField> _fields;
        private readonly Stack<List<HInline>> _sinks = new();
        private readonly HParaFormat _format;
        private readonly int _heading;
        private readonly HListRef? _list;
        private bool _afterTable;

        public ParagraphBuilder(List<HBlock> output, List<OpenField> fields, HParaFormat format, int heading, HListRef? list)
        {
            _output = output;
            _fields = fields;
            _format = format;
            _heading = heading;
            _list = list;
            Current = NewParagraph(list);
        }

        public HParagraph Current { get; private set; }

        /// <summary>Format of the last run, used for the paragraph mark (the height of an empty paragraph).</summary>
        public HCharFormat MarkFormat { get; set; }

        private List<HInline> Sink => _sinks.Count > 0 ? _sinks.Peek() : Current.Inlines;

        public void Add(HInline inline) => Sink.Add(inline);

        /// <summary>Adds text, joined to the previous text when it has the same format.</summary>
        public void AddText(string text, HCharFormat format)
        {
            if (text.Length == 0)
                return;
            var sink = Sink;
            if (sink.Count > 0 && sink[^1] is HText last && last.Format == format)
                sink[^1] = new HText(last.Text + text, format);
            else
                sink.Add(new HText(text, format));
        }

        public void OpenLink(string target)
        {
            var link = new HLink(target);
            Sink.Add(link);
            _sinks.Push(link.Content);
        }

        public void CloseLink()
        {
            if (_sinks.Count > 0)
                _sinks.Pop();
        }

        public void Table(HTable table)
        {
            // Breaks and column changes have no place on a table, so they keep an empty paragraph in front of it.
            if (HasContent || Current.PageBreakBefore || Current.ColumnBreakBefore || Current.ColumnsChange is not null)
                Emit();
            _output.Add(table);
            // The rest of a list item is not numbered again.
            Current = NewParagraph(_list is null ? null : _list with { Numbered = false });
            _afterTable = true;
        }

        public void Finish()
        {
            if (!_afterTable || HasContent)
                Emit();
        }

        private bool HasContent => Current.Inlines.Any(i => i is not HLink { Content.Count: 0 });

        private void Emit()
        {
            Current.MarkFormat = MarkFormat;
            _output.Add(Current);
        }

        private HParagraph NewParagraph(HListRef? list)
        {
            Current = new HParagraph { Format = _format, HeadingLevel = _heading, List = list };
            _sinks.Clear();
            // A hyperlink that started in an earlier paragraph (or before a table) continues here.
            foreach (var field in _fields.Where(f => f.Target is not null))
                OpenLink(field.Target!);
            return Current;
        }
    }

    private void ReadParagraph(XElement p, List<HBlock> output, SectionState? section)
    {
        var shape = ParaShapeOf(Attr(p, "paraPrIDRef"));
        var (heading, list) = HeadingAndList(shape, Attr(p, "styleIDRef"));
        var builder = new ParagraphBuilder(output, _fields, shape.Format, heading, list) { MarkFormat = CharFormat(null) };
        builder.Current.PageBreakBefore = Flag(p, "pageBreak") || shape.PageBreakBefore;
        builder.Current.ColumnBreakBefore = Flag(p, "columnBreak");

        foreach (var child in Elements(p))
        {
            switch (child.Name.LocalName)
            {
                case "run":
                    Run(child, builder, section);
                    break;
                case "linesegarray":
                    break; // layout cache, computed again by every reader
                default:
                    Skip(child);
                    break;
            }
        }
        builder.Finish();
    }

    /// <summary>
    /// Heading level and list of a paragraph shape. Outline paragraphs (개요) are headings; when the section's outline
    /// numbering shows a number for their level (1., 가., ...), they are numbered too.
    /// </summary>
    private (int Heading, HListRef? List) HeadingAndList(ParaShape shape, string? styleId)
    {
        var heading = 0;
        HListRef? list = null;
        switch (shape.HeadingType)
        {
            case "OUTLINE":
                {
                    heading = Math.Min(shape.HeadingLevel + 1, 9);
                    var numbering = Numbering(shape.HeadingId is { Length: > 0 } id && id != "0" ? id : _outlineNumberingId);
                    if (numbering is not null && shape.HeadingLevel < numbering.Levels.Count && HasCounter(numbering.Levels[shape.HeadingLevel].Text))
                        list = new HListRef(numbering, shape.HeadingLevel, Numbered: true);
                    break;
                }
            case "NUMBER" or "NUMBERING" or "BULLET":
                {
                    var numbering = shape.HeadingType == "BULLET" ? Bullet(shape.HeadingId) : Numbering(shape.HeadingId);
                    if (numbering is { Levels.Count: > 0 })
                        list = new HListRef(numbering, Math.Min(shape.HeadingLevel, numbering.Levels.Count - 1), Numbered: true);
                    break;
                }
        }
        return (heading > 0 ? heading : StyleHeadingLevel(styleId), list);
    }

    // ───────────────────────── Runs ─────────────────────────

    private void Run(XElement run, ParagraphBuilder builder, SectionState? section)
    {
        var format = CharFormat(Attr(run, "charPrIDRef"));
        builder.MarkFormat = format;
        foreach (var item in Elements(run))
        {
            switch (item.Name.LocalName)
            {
                case "t":
                    Text(item, builder, format);
                    break;
                case "secPr":
                    break; // read by ReadTopParagraph
                case "ctrl":
                    foreach (var control in Elements(item))
                        Control(control, builder, format, section);
                    break;
                case "tbl":
                    builder.Table(Table(item));
                    break;
                case "colPr" or "header" or "footer" or "footNote" or "endNote" or "fieldBegin" or "fieldEnd" or "bookmark" or "autoNum" or "newNum" or "pageNum":
                    Control(item, builder, format, section); // controls outside hp:ctrl (other producers)
                    break;
                default:
                    DrawingObject(item, builder, format);
                    break;
            }
        }
    }

    /// <summary>hp:t: text mixed with tabs, line breaks, special spaces, highlighter and change-tracking marks.</summary>
    private void Text(XElement t, ParagraphBuilder builder, HCharFormat format)
    {
        foreach (var node in t.Nodes())
        {
            if (node is XText text)
            {
                var value = text.Value;
                // 한글 never stores a line break as a character (hp:lineBreak does that), so line ends are only the
                // indentation of pretty-printed files.
                if (value.Contains('\n') || value.Contains('\r'))
                {
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    value = value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                }
                if (!_deleted)
                    builder.AddText(value, Highlighted(format));
                continue;
            }
            if (node is not XElement element)
                continue;
            switch (element.Name.LocalName)
            {
                case "tab":
                    if (!_deleted)
                        builder.Add(new HTab(Highlighted(format)));
                    break;
                case "lineBreak" or "columnBreak":
                    if (!_deleted)
                        builder.Add(new HLineBreak(format));
                    break;
                case "nbSpace":
                    if (!_deleted)
                        builder.AddText(" ", Highlighted(format));
                    break;
                case "fwSpace":
                    if (!_deleted)
                        builder.AddText(" ", Highlighted(format)); // fixed-width space: as wide as a digit
                    break;
                case "hyphen":
                    if (!_deleted)
                        builder.AddText("­", Highlighted(format)); // soft hyphen, shown only at a line end
                    break;
                case "markpenBegin":
                    _markpen = Color(Attr(element, "color")) ?? "#FFFF00";
                    break;
                case "markpenEnd":
                    _markpen = null;
                    break;
                case "deleteBegin":
                    _deleted = true;
                    break;
                case "deleteEnd":
                    _deleted = false;
                    break;
                case "insertBegin" or "insertEnd" or "titleMark":
                    break; // inserted text is shown as it is; title marks only feed a table of contents
                default:
                    Skip(element);
                    break;
            }
        }
    }

    private HCharFormat Highlighted(HCharFormat format) => _markpen is null ? format : format with { Shade = _markpen };

    // ───────────────────────── Controls ─────────────────────────

    private void Control(XElement control, ParagraphBuilder builder, HCharFormat format, SectionState? section)
    {
        switch (control.Name.LocalName)
        {
            case "colPr" when section is not null:
                if (section.ColumnsSet)
                {
                    builder.Current.ColumnsChange = Columns(control);
                }
                else
                {
                    section.Current.Columns = Columns(control);
                    section.ColumnsSet = true;
                }
                break;
            case "header" or "footer" when section is not null:
                HeaderFooter(control, section.Current);
                break;
            case "footNote" or "endNote":
                builder.Add(Note(control, endnote: control.Name.LocalName == "endNote"));
                break;
            case "fieldBegin":
                FieldBegin(control, builder);
                break;
            case "fieldEnd":
                FieldEnd(control, builder);
                break;
            case "bookmark":
                if (Attr(control, "name") is { Length: > 0 } name)
                    builder.Add(new HBookmark(name));
                break;
            case "autoNum":
                switch (Attr(control, "numType"))
                {
                    case "PAGE":
                        builder.Add(new HField(HFieldKind.PageNumber, format));
                        break;
                    case "TOTAL_PAGE":
                        builder.Add(new HField(HFieldKind.TotalPages, format));
                        break;
                    case "FOOTNOTE" or "ENDNOTE":
                        break; // the note's own number, written again by every target format
                    default:
                        // Picture, table and equation numbers: keep the number shown when the file was saved.
                        builder.AddText(Attr(control, "num") ?? "", format);
                        break;
                }
                break;
            case "newNum" when section is not null:
                if (Attr(control, "numType") == "PAGE" && !section.StartNumberSet)
                {
                    section.Current.StartPageNumber = Math.Max(1, Int(control, "num", 1));
                    section.StartNumberSet = true;
                }
                break;
            case "pageNum" when section is not null:
                section.PageNumber = control;
                break;
            default:
                Skip(control); // hidden comments, index marks, page hiding, page number control, ...
                break;
        }
    }

    /// <summary>A header or footer; 한글 lets a later one replace it from its page on, the model keeps the first.</summary>
    private void HeaderFooter(XElement control, HSection section)
    {
        var pages = Attr(control, "applyPageType") switch
        {
            "EVEN" => HPageType.Even,
            "ODD" => HPageType.Odd,
            _ => HPageType.Both,
        };
        var target = control.Name.LocalName == "header" ? section.Headers : section.Footers;
        if (target.Any(h => h.Pages == pages))
        {
            Skip(control);
            return;
        }
        var item = new HHeaderFooter(pages);
        item.Blocks.AddRange(ReadSubList(Child(control, "subList")));
        target.Add(item);
    }

    private HNote Note(XElement control, bool endnote)
    {
        var note = new HNote(endnote);
        note.Blocks.AddRange(ReadSubList(Child(control, "subList")));
        // The note starts with its number (an autoNum, skipped) and a space.
        if (note.Blocks.FirstOrDefault() is HParagraph first && first.Inlines.FirstOrDefault() is HText { Text: [' ', ..] } lead)
        {
            if (lead.Text.Length == 1)
                first.Inlines.RemoveAt(0);
            else
                first.Inlines[0] = new HText(lead.Text[1..], lead.Format);
        }
        return note;
    }

    // ───────────────────────── Fields ─────────────────────────

    private void FieldBegin(XElement control, ParagraphBuilder builder)
    {
        string? target = null;
        switch (Attr(control, "type"))
        {
            case "HYPERLINK":
                target = HyperlinkTarget(control);
                break;
            case "BOOKMARK":
                if (Attr(control, "name") is { Length: > 0 } name)
                    builder.Add(new HBookmark(name));
                break;
        }
        // Other fields (click-here, date, file name, ...) keep their result text, which follows until fieldEnd.
        _fields.Add(new OpenField(Attr(control, "id"), target));
        if (target is not null)
            builder.OpenLink(target);
    }

    private void FieldEnd(XElement control, ParagraphBuilder builder)
    {
        var id = Attr(control, "beginIDRef");
        var index = _fields.FindLastIndex(f => id is null || f.Id == id);
        if (index < 0)
            index = _fields.Count - 1;
        if (index < 0)
            return;
        var field = _fields[index];
        _fields.RemoveAt(index);
        if (field.Target is not null)
            builder.CloseLink();
    }

    /// <summary>
    /// The target of a HYPERLINK field, from its "Command" parameter: the target with ':', '?', ';' and '#' escaped
    /// by a backslash, then ";kind;..." ("url;1;0;0;"). An unescaped '?' starts a bookmark name ("?name" = a
    /// bookmark in this document, "file.hwp?name" one in another file).
    /// </summary>
    internal static string? HyperlinkTarget(XElement fieldBegin)
    {
        var parameters = Descendants(fieldBegin, "stringParam").ToList();
        string? Parameter(string name) => parameters.FirstOrDefault(p => Attr(p, "name") == name)?.Value;

        if (Parameter("Command") is { Length: > 0 } command)
        {
            var target = new StringBuilder();
            for (var i = 0; i < command.Length; i++)
            {
                var c = command[i];
                if (c == '\\' && i + 1 < command.Length)
                {
                    target.Append(command[++i]);
                    continue;
                }
                if (c == ';')
                    break;
                // Producers that do not escape keep a URL's query string ('?') as it is.
                if (c == '?' && !target.ToString().Contains("://", StringComparison.Ordinal))
                    c = '#';
                target.Append(c);
            }
            if (target.Length > 0 && target.ToString() != "#")
                return target.ToString();
        }
        return Parameter("Path") is { Length: > 0 } path ? path : null;
    }

    // ───────────────────────── Tables ─────────────────────────

    private HTable Table(XElement tbl)
    {
        var table = new HTable { ColumnCount = Math.Max(0, Int(tbl, "colCnt")) };
        var pos = Child(tbl, "pos");
        if (pos is not null && !Flag(pos, "treatAsChar"))
        {
            table.Align = Attr(pos, "horzAlign") switch
            {
                "CENTER" => HAlign.Center,
                "RIGHT" => HAlign.Right,
                _ => HAlign.Left,
            };
            if (table.Align == HAlign.Left && Attr(pos, "horzRelTo") is null or "COLUMN" or "PARA")
                table.Indent = Math.Max(0, Int(pos, "horzOffset"));
        }
        if (Child(tbl, "inMargin") is { } inMargin)
            table.CellMargin = new HInsets(Int(inMargin, "left"), Int(inMargin, "right"), Int(inMargin, "top"), Int(inMargin, "bottom"));
        table.Borders = BorderFill(Attr(tbl, "borderFillIDRef"))?.Borders;

        var widths = new List<(int Column, int Span, int Width)>();
        var cellMargins = new HashSet<HInsets?>();
        var headerRows = true;
        foreach (var tr in Children(tbl, "tr"))
        {
            var row = new HRow();
            int? height = null;
            var allHeader = true;
            foreach (var tc in Children(tr, "tc"))
            {
                // Some producers wrap the cell properties in hp:tcPr.
                XElement? Part(string name) => Child(tc, name) ?? Child(Child(tc, "tcPr"), name);
                var address = Part("cellAddr");
                var span = Part("cellSpan");
                var size = Part("cellSz");
                var subList = Child(tc, "subList");
                var cell = new HCell
                {
                    Column = address is null ? -1 : Math.Max(0, Int(address, "colAddr")),
                    ColSpan = Math.Max(1, Int(span, "colSpan", 1)),
                    RowSpan = Math.Max(1, Int(span, "rowSpan", 1)),
                    VerticalAlign = VerticalAlign(Attr(subList, "vertAlign")),
                };
                if (BorderFill(Attr(tc, "borderFillIDRef")) is { } borderFill)
                    (cell.Borders, cell.Fill) = borderFill;
                cell.Blocks.AddRange(ReadSubList(subList));
                row.Cells.Add(cell);

                if (cell.RowSpan == 1 && Int(size, "height") is > 0 and var h)
                    height = Math.Min(height ?? int.MaxValue, h);
                if (cell.Column >= 0)
                    widths.Add((cell.Column, cell.ColSpan, Int(size, "width")));
                allHeader &= Flag(tc, "header");
                cellMargins.Add(Flag(tc, "hasMargin") && Part("cellMargin") is { } margin
                    ? new HInsets(Int(margin, "left"), Int(margin, "right"), Int(margin, "top"), Int(margin, "bottom"))
                    : null);
            }
            if (row.Cells.Count == 0)
                continue;
            row.Height = height;
            // Header rows (제목 줄) are the leading rows whose cells are all marked as header cells.
            headerRows &= allHeader;
            row.Header = headerRows;
            table.Rows.Add(row);
        }

        // The model has one cell padding per table: cells with their own margins (hasMargin) win when they all agree.
        if (cellMargins.Count == 1 && cellMargins.Single() is { } shared)
            table.CellMargin = shared;
        table.ColumnCount = Math.Max(table.ColumnCount, widths.Select(w => w.Column + w.Span).DefaultIfEmpty(1).Max());
        table.ColumnWidths = ColumnWidths(widths, table.ColumnCount);
        if (Child(Child(tbl, "caption"), "subList") is { } caption)
            table.Caption.AddRange(ReadSubList(caption));
        return table;
    }

    /// <summary>Column widths from the cell widths: single cells first, then what spanning cells leave over.</summary>
    private static int[]? ColumnWidths(List<(int Column, int Span, int Width)> cells, int columns)
    {
        if (columns <= 0)
            return null;
        var widths = new int?[columns];
        foreach (var (column, span, width) in cells)
        {
            if (span == 1 && column < columns && width > 0)
                widths[column] ??= width;
        }
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (column, span, width) in cells.Where(c => c.Span > 1 && c.Width > 0))
            {
                var range = Enumerable.Range(column, Math.Min(span, columns - column)).ToList();
                var unknown = range.Where(i => widths[i] is null).ToList();
                if (unknown.Count == 0)
                    continue;
                var rest = width - range.Sum(i => widths[i] ?? 0);
                foreach (var i in unknown)
                    widths[i] = Math.Max(1, rest / unknown.Count);
                changed = true;
            }
        }
        if (widths.All(w => w is null))
            return null;
        var fallback = (int)widths.Where(w => w is not null).Average(w => w!.Value);
        return widths.Select(w => w ?? fallback).ToArray();
    }

    private static HVerticalAlign VerticalAlign(string? value) => value switch
    {
        "CENTER" => HVerticalAlign.Center,
        "BOTTOM" => HVerticalAlign.Bottom,
        _ => HVerticalAlign.Top,
    };
}
