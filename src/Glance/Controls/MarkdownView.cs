using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Markdig.Syntax.Block;
using Table = Markdig.Extensions.Tables.Table;
using TableRow = Markdig.Extensions.Tables.TableRow;
using TableCell = Markdig.Extensions.Tables.TableCell;

namespace Glance;

/// Markdown parsed by Markdig, drawn as plain themed WPF elements (no document viewer: cards stay draggable and match the palette).
static class MarkdownView
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UsePipeTables().UseAutoLinks().Build();

    static Brush R(string key) => (Brush)Application.Current.Resources[key];

    /// toggle gets the source index of a clicked task box ("[ ]"); Toggle(text, index) flips it.
    public static FrameworkElement Render(string text, Action<int> toggle) => Blocks(Markdig.Markdown.Parse(text, Pipeline), toggle);

    /// Every task box in the text, as the source indexes Render hands to toggle.
    public static int[] Tasks(string text) => [.. Markdig.Markdown.Parse(text, Pipeline).Descendants<TaskList>().Select(t => t.Span.Start)];

    /// Flips the "[ ]" / "[x]" at or just after index (same length, so other indexes stay valid).
    public static string Toggle(string text, int at)
    {
        var i = text.IndexOf('[', at);
        if (i < 0 || i + 2 >= text.Length || text[i + 2] != ']') return text;
        return string.Concat(text.AsSpan(0, i + 1), text[i + 1] == ' ' ? "x" : " ", text.AsSpan(i + 2));
    }

    /// Children stacked with a gap between them (none after the last).
    static StackPanel Blocks(ContainerBlock c, Action<int> toggle)
    {
        var panel = new StackPanel();
        foreach (var b in c)
        {
            var e = Draw(b, toggle);
            e.Margin = new(0, b is HeadingBlock && panel.Children.Count > 0 ? 6 : 0, 0, b == c.LastChild ? 0 : 6);
            panel.Children.Add(e);
        }
        return panel;
    }

    static FrameworkElement Draw(Block b, Action<int> toggle) => b switch
    {
        HeadingBlock h => Text(h.Inline, h.Level switch { 1 => 20, 2 => 17, _ => 15 }, FontWeights.SemiBold),
        ParagraphBlock p => Text(p.Inline),
        ListBlock l => DrawList(l, toggle),
        QuoteBlock q => new Border
        {
            BorderBrush = R("Accent"), BorderThickness = new(2, 0, 0, 0), Padding = new(10, 0, 0, 0),
            Child = Blocks(q, toggle), Opacity = 0.8,
        },
        CodeBlock k => new Border
        {
            Background = R("Track"), CornerRadius = new(6), Padding = new(8, 6, 8, 6),
            Child = new TextBlock { Text = k.Lines.ToString(), FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
        },
        ThematicBreakBlock => new Border { Height = 1, Background = R("Line") },
        Table t => DrawTable(t, toggle),
        LeafBlock l => new TextBlock { Text = l.Lines.ToString(), TextWrapping = TextWrapping.Wrap }, // raw HTML and the like
        ContainerBlock c => Blocks(c, toggle),
        _ => new TextBlock(),
    };

    static TextBlock Text(ContainerInline? inline, double size = 14, FontWeight? weight = null)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        if (inline is not null) Add(t.Inlines, inline);
        return t;
    }

    static void Add(InlineCollection to, ContainerInline from)
    {
        foreach (var i in from)
            switch (i)
            {
                case TaskList: break; // drawn as the list item's box
                case LiteralInline l: to.Add(new Run(l.Content.ToString())); break;
                case CodeInline k: to.Add(new Run(k.Content) { FontFamily = (FontFamily)Application.Current.Resources["Mono"], Background = R("Track") }); break;
                case LineBreakInline: to.Add(new LineBreak()); break; // soft breaks too: a note keeps the lines as typed
                case AutolinkInline a: to.Add(Link(a.IsEmail ? "mailto:" + a.Url : a.Url, a.Url)); break;
                case LinkInline { IsImage: false } k:
                    var h = Link(k.Url, null);
                    Add(h.Inlines, k);
                    to.Add(h);
                    break;
                case EmphasisInline e:
                    var s = new Span();
                    if (e.DelimiterChar == '~') s.TextDecorations = TextDecorations.Strikethrough;
                    else if (e.DelimiterCount >= 2) s.FontWeight = FontWeights.Bold;
                    else s.FontStyle = FontStyles.Italic;
                    Add(s.Inlines, e);
                    to.Add(s);
                    break;
                case ContainerInline c: Add(to, c); break; // images show their alt text
                case HtmlEntityInline e: to.Add(new Run(e.Transcoded.ToString())); break;
                case HtmlInline e: to.Add(new Run(e.Tag)); break;
            }
    }

    /// Opens on press: the card under it would otherwise take the click for dragging. Only web and mail links open.
    static Hyperlink Link(string? url, string? text)
    {
        var h = new Hyperlink { Foreground = R("Accent"), Cursor = Cursors.Hand, ToolTip = url };
        if (text is not null) h.Inlines.Add(new Run(text));
        h.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto")
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        };
        return h;
    }

    static StackPanel DrawList(ListBlock l, Action<int> toggle)
    {
        var panel = new StackPanel();
        var n = int.TryParse(l.OrderedStart, out var start) ? start : 1;
        foreach (var item in l.OfType<ListItemBlock>())
        {
            var content = Blocks(item, toggle);
            FrameworkElement mark;
            if ((item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild is TaskList task)
            {
                var box = new CheckBox
                {
                    Style = (Style)Application.Current.Resources["Check"], IsChecked = task.Checked, VerticalAlignment = VerticalAlignment.Top,
                    LayoutTransform = new ScaleTransform(0.8, 0.8), Margin = new(0, 1, 8, 0),
                };
                var at = task.Span.Start;
                content.Opacity = task.Checked ? 0.55 : 1;
                box.Click += (_, _) => { content.Opacity = box.IsChecked == true ? 0.55 : 1; toggle(at); };
                mark = box;
            }
            else mark = new TextBlock { Text = l.IsOrdered ? $"{n}." : "•", Foreground = R("Sub"), Margin = new(0, 0, 8, 0) };
            n++;
            DockPanel.SetDock(mark, Dock.Left);
            panel.Children.Add(new DockPanel { Margin = new(0, 0, 0, 3), Children = { mark, content } });
        }
        return panel;
    }

    static Grid DrawTable(Table t, Action<int> toggle)
    {
        var g = new Grid();
        var r = 0;
        foreach (var row in t.OfType<TableRow>())
        {
            g.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var c = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                while (g.ColumnDefinitions.Count <= c) g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var e = Blocks(cell, toggle);
                e.Margin = new(0, 2, 12, 2);
                if (row.IsHeader) TextElement.SetFontWeight(e, FontWeights.SemiBold);
                Grid.SetRow(e, r);
                Grid.SetColumn(e, c++);
                g.Children.Add(e);
            }
            r++;
        }
        return g;
    }
}
