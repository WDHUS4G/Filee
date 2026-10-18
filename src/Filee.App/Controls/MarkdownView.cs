// Shows a small Markdown document with the app's styles: headings, paragraphs, lists, tables, links, code.
// Made for the third-party notices on the About page, not as a general Markdown viewer (no images or HTML).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Filee.App.Controls;

/// <summary>Renders <see cref="Markdown"/> as a stack of text blocks, tables and link buttons.</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>Skips the first level-1 heading (the page around the view shows its own title).</summary>
    public static readonly StyledProperty<bool> SkipTitleProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(SkipTitle));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().UseEmphasisExtras().Build();

    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Menlo, monospace");

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
        SkipTitleProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView() => Spacing = 10;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool SkipTitle
    {
        get => GetValue(SkipTitleProperty);
        set => SetValue(SkipTitleProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(Markdown))
            return;
        var skipTitle = SkipTitle;
        foreach (var block in Markdig.Markdown.Parse(Markdown, Pipeline))
        {
            if (skipTitle && block is HeadingBlock { Level: 1 })
            {
                skipTitle = false;
                continue;
            }
            if (Create(block) is { } control)
                Children.Add(control);
        }
    }

    private static Control? Create(Block block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Text(paragraph.Inline),
        ListBlock list => List(list),
        Table table => TableGrid(table),
        QuoteBlock quote => Quote(quote),
        CodeBlock code => Code(code),
        ThematicBreakBlock => Themed(new Border { Height = 1, Margin = new Thickness(0, 4) }, Border.BackgroundProperty, "SubtleBorderBrush"),
        _ => null,
    };

    private static Control Heading(HeadingBlock heading)
    {
        var text = Text(heading.Inline);
        text.FontWeight = FontWeight.SemiBold;
        text.FontSize = heading.Level switch { 1 => 18, 2 => 14.5, _ => 13.5 };
        text.Margin = new Thickness(0, heading.Level <= 2 ? 8 : 4, 0, 0);
        return text;
    }

    /// <summary>A selectable paragraph; links become inline buttons that open the browser.</summary>
    private static SelectableTextBlock Text(ContainerInline? inlines)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        if (inlines is not null)
            AddInlines(block.Inlines!, inlines, bold: false, italic: false);
        return block;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container, bool bold, bool italic)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(Styled(new Run(literal.Content.ToString()), bold, italic));
                    break;
                case CodeInline code:
                    target.Add(Styled(new Run(code.Content) { FontFamily = Monospace }, bold, italic));
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case EmphasisInline emphasis:
                    AddInlines(target, emphasis,
                        bold || emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2,
                        italic || emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount is 1 or 3);
                    break;
                case LinkInline { IsImage: false } link when Uri.TryCreate(link.Url, UriKind.Absolute, out var uri):
                    target.Add(new InlineUIContainer(Link(PlainText(link), uri)) { BaselineAlignment = BaselineAlignment.TextBottom });
                    break;
                case AutolinkInline autolink when Uri.TryCreate(autolink.Url, UriKind.Absolute, out var uri):
                    target.Add(new InlineUIContainer(Link(autolink.Url, uri)) { BaselineAlignment = BaselineAlignment.TextBottom });
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case ContainerInline other:
                    AddInlines(target, other, bold, italic);
                    break;
            }
        }
    }

    private static Run Styled(Run run, bool bold, bool italic)
    {
        if (bold)
            run.FontWeight = FontWeight.SemiBold;
        if (italic)
            run.FontStyle = FontStyle.Italic;
        return run;
    }

    private static HyperlinkButton Link(string text, Uri uri)
    {
        var button = new HyperlinkButton
        {
            NavigateUri = uri,
            Padding = new Thickness(0),
            Content = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
        };
        ToolTip.SetTip(button, uri.AbsoluteUri);
        return button;
    }

    private static string PlainText(ContainerInline container) =>
        string.Concat(container.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    private static Control List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0, 0, 0) };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var content = new StackPanel { Spacing = 4 };
            foreach (var block in item)
                if (Create(block) is { } control)
                    content.Children.Add(control);
            var marker = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = 13, MinWidth = 18 };
            var row = new DockPanel();
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>A grid with a bold header row and a line between rows; column widths follow the text lengths.</summary>
    private static Control TableGrid(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            var longest = rows.Select(r => c < r.Count ? PlainLength((TableCell)r[c]) : 0).DefaultIfEmpty(0).Max();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Clamp(longest, 8, 40), GridUnitType.Star)));
        }
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cells = rows[r].OfType<TableCell>().ToList();
            for (var c = 0; c < cells.Count && c < columns; c++)
            {
                var content = Cell(cells[c], rows[r].IsHeader);
                var border = new Border
                {
                    Child = content,
                    Padding = new Thickness(8, 6),
                    BorderThickness = new Thickness(0, 0, 0, r == rows.Count - 1 ? 0 : 1),
                };
                Themed(border, Border.BorderBrushProperty, "SubtleBorderBrush");
                if (rows[r].IsHeader)
                    Themed(border, Border.BackgroundProperty, "SurfaceAltBrush");
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        var frame = new Border
        {
            Child = grid,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
        };
        return Themed(frame, Border.BorderBrushProperty, "SubtleBorderBrush");
    }

    private static Control Cell(TableCell cell, bool header)
    {
        // A cell holding only a URL becomes a link button (the "Project" column of the notices).
        var inline = cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline;
        var only = inline?.FirstChild is { } first && first.NextSibling is null ? first : null;
        if (only is AutolinkInline autolink && Uri.TryCreate(autolink.Url, UriKind.Absolute, out var autoUri))
            return Link(autolink.Url, autoUri);
        if (only is LinkInline { IsImage: false } link && Uri.TryCreate(link.Url, UriKind.Absolute, out var linkUri))
            return Link(PlainText(link), linkUri);

        var text = Text(inline);
        text.FontSize = 12.5;
        if (header)
            text.FontWeight = FontWeight.SemiBold;
        return text;
    }

    private static int PlainLength(TableCell cell) =>
        cell.Descendants<LiteralInline>().Sum(l => l.Content.Length) + cell.Descendants<AutolinkInline>().Sum(a => a.Url.Length);

    private static Control Quote(QuoteBlock quote)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in quote)
            if (Create(block) is { } control)
                panel.Children.Add(control);
        var border = new Border { Child = panel, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2) };
        return Themed(border, Border.BorderBrushProperty, "AccentSoftBrush");
    }

    private static Control Code(CodeBlock code)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Child = new SelectableTextBlock
            {
                Text = code.Lines.ToString(),
                FontFamily = Monospace,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        return Themed(border, Border.BackgroundProperty, "SurfaceAltBrush");
    }

    /// <summary>Binds a brush property to a theme resource, so light / dark and accent changes apply live.</summary>
    private static T Themed<T>(T control, AvaloniaProperty property, string resource) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(resource));
        return control;
    }
}
