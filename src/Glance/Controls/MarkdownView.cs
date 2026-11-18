using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Markdig.Syntax.Block;
using Table = Markdig.Extensions.Tables.Table;
using TableRow = Markdig.Extensions.Tables.TableRow;
using TableCell = Markdig.Extensions.Tables.TableCell;

namespace Glance;

/// Markdown parsed by Markdig, drawn as plain themed WPF elements (no document viewer: cards stay draggable and match the palette).
/// Beyond CommonMark: tasks, tables, strikethrough, [[wikilinks]], #tags, due dates (@2026-11-20 17:00) and > [!tip] callouts.
static partial class MarkdownView
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UsePipeTables().UseAutoLinks().Build();

    [GeneratedRegex(@"\[\[([^\[\]|\r\n]+)(?:\|([^\[\]\r\n]+))?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"(?<![\w#&/])#([\p{L}\p{N}_/-]*\p{L}[\p{L}\p{N}_/-]*)|@(\d{4}-\d{2}-\d{2} \d{2}:\d{2})")]
    private static partial Regex TagOrDue();

    [GeneratedRegex(@"```[\s\S]*?```|`[^`\r\n]*`")]
    private static partial Regex Code();

    [GeneratedRegex(@"^\s*>\s*\[!(\w+)\]([+-]?)[ \t]*(.*)$")]
    private static partial Regex CalloutHead();

    /// What a drawing calls back into: tick the n-th task, follow a [[link]], pick a #tag.
    sealed class Ctx(string source, Action<int> toggle, Action<string>? go, Action<string>? tag)
    {
        public readonly string Source = source;
        public readonly Action<int> Toggle = toggle;
        public readonly Action<string>? Go = go;
        public readonly Action<string>? Tag = tag;
        public int Tasks; // numbers task boxes in document order, as Toggle counts them
    }

    static Brush R(string key) => (Brush)Application.Current.Resources[key];

    /// toggle gets the clicked task box's number (0 = first in the text); Toggle(text, n) flips it.
    public static FrameworkElement Render(string text, Action<int> toggle, Action<string>? go = null, Action<string>? tag = null)
    {
        // [[Target|Alias]] → a link the drawing recognizes. Task numbering is unaffected: links never make or break list items.
        var source = WikiLink().Replace(text, m =>
            $"[{(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value)}](<glance:{Uri.EscapeDataString(m.Groups[1].Value.Trim())}>)");
        var ctx = new Ctx(source, toggle, go, tag);
        return Blocks(Markdig.Markdown.Parse(source, Pipeline), ctx);
    }

    /// Each task's number, whether it's ticked, and its text (after the box, to the end of its line).
    public static IEnumerable<(int Ordinal, bool Done, string Text)> TaskLines(string text)
    {
        var n = 0;
        foreach (var t in Markdig.Markdown.Parse(text, Pipeline).Descendants<TaskList>())
        {
            var from = Math.Min(t.Span.End + 1, text.Length);
            var end = text.IndexOfAny(['\r', '\n'], from);
            yield return (n++, t.Checked, text[from..(end < 0 ? text.Length : end)]);
        }
    }

    /// Flips the n-th task's "[ ]" / "[x]" (same length, so the others stay put).
    public static string Toggle(string text, int n)
    {
        var task = Markdig.Markdown.Parse(text, Pipeline).Descendants<TaskList>().ElementAtOrDefault(n);
        if (task is null) return text;
        var i = text.IndexOf('[', task.Span.Start);
        if (i < 0 || i + 2 >= text.Length || text[i + 2] != ']') return text;
        return string.Concat(text.AsSpan(0, i + 1), text[i + 1] == ' ' ? "x" : " ", text.AsSpan(i + 2));
    }

    /// The #tags in a text, lowercased, without the #.
    public static IEnumerable<string> Tags(string text) =>
        TagOrDue().Matches(Code().Replace(text, " ")).Where(m => m.Groups[1].Success).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct();

    /// The [[link]] targets in a text.
    public static IEnumerable<string> Links(string text) => WikiLink().Matches(text).Select(m => m.Groups[1].Value.Trim());

    /// Children stacked with a gap between them (none after the last).
    static StackPanel Blocks(ContainerBlock c, Ctx ctx)
    {
        var panel = new StackPanel();
        foreach (var b in c)
        {
            var e = Draw(b, ctx);
            e.Margin = new(0, b is HeadingBlock && panel.Children.Count > 0 ? 6 : 0, 0, b == c.LastChild ? 0 : 6);
            panel.Children.Add(e);
        }
        return panel;
    }

    static FrameworkElement Draw(Block b, Ctx ctx) => b switch
    {
        HeadingBlock h => Text(h.Inline, ctx, h.Level switch { 1 => 20, 2 => 17, _ => 15 }, FontWeights.SemiBold),
        ParagraphBlock p => Text(p.Inline, ctx),
        ListBlock l => DrawList(l, ctx),
        QuoteBlock q => Callout(q, ctx) ?? new Border
        {
            BorderBrush = R("Accent"), BorderThickness = new(2, 0, 0, 0), Padding = new(10, 0, 0, 0),
            Child = Blocks(q, ctx), Opacity = 0.8,
        },
        CodeBlock k => new Border
        {
            Background = R("Track"), CornerRadius = new(6), Padding = new(8, 6, 8, 6),
            Child = new TextBlock { Text = k.Lines.ToString(), FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
        },
        ThematicBreakBlock => new Border { Height = 1, Background = R("Line") },
        Table t => DrawTable(t, ctx),
        LeafBlock l => new TextBlock { Text = l.Lines.ToString(), TextWrapping = TextWrapping.Wrap }, // raw HTML and the like
        ContainerBlock c => Blocks(c, ctx),
        _ => new TextBlock(),
    };

    /// skipFirstLine: a callout's first line is its header, drawn separately.
    static TextBlock Text(ContainerInline? inline, Ctx ctx, double size = 14, FontWeight? weight = null, bool skipFirstLine = false)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        if (inline is not null) Add(t.Inlines, inline, ctx, skipFirstLine);
        return t;
    }

    static void Add(InlineCollection to, ContainerInline from, Ctx ctx, bool skipFirstLine = false)
    {
        var literal = ""; // neighboring literals joined, so a #tag split by the parser is still seen whole
        void Flush()
        {
            if (literal.Length > 0) Literal(to, literal, ctx);
            literal = "";
        }
        foreach (var i in from)
        {
            if (skipFirstLine)
            {
                if (i is LineBreakInline) skipFirstLine = false;
                continue;
            }
            if (i is LiteralInline l) { literal += l.Content.ToString(); continue; }
            Flush();
            switch (i)
            {
                case TaskList: break; // drawn as the list item's box
                case CodeInline k: to.Add(new Run(k.Content) { FontFamily = (FontFamily)Application.Current.Resources["Mono"], Background = R("Track") }); break;
                case LineBreakInline: to.Add(new LineBreak()); break; // soft breaks too: a note keeps the lines as typed
                case AutolinkInline a: to.Add(Link(a.IsEmail ? "mailto:" + a.Url : a.Url, a.Url)); break;
                case LinkInline { IsImage: false } k when k.Url?.StartsWith("glance:") == true:
                    var target = Uri.UnescapeDataString(k.Url["glance:".Length..]);
                    var w = new Hyperlink { Foreground = R("Accent"), Cursor = Cursors.Hand, ToolTip = $"Go to {target}", TextDecorations = null, FontWeight = FontWeights.SemiBold };
                    w.MouseLeftButtonDown += (_, e) => { e.Handled = true; ctx.Go?.Invoke(target); };
                    Add(w.Inlines, k, ctx);
                    to.Add(w);
                    break;
                case LinkInline { IsImage: false } k:
                    var h = Link(k.Url, null);
                    Add(h.Inlines, k, ctx);
                    to.Add(h);
                    break;
                case EmphasisInline e:
                    var s = new Span();
                    if (e.DelimiterChar == '~') s.TextDecorations = TextDecorations.Strikethrough;
                    else if (e.DelimiterCount >= 2) s.FontWeight = FontWeights.Bold;
                    else s.FontStyle = FontStyles.Italic;
                    Add(s.Inlines, e, ctx);
                    to.Add(s);
                    break;
                case ContainerInline c: Add(to, c, ctx); break; // images show their alt text
                case HtmlEntityInline e: to.Add(new Run(e.Transcoded.ToString())); break;
                case HtmlInline e: to.Add(new Run(e.Tag)); break;
            }
        }
        Flush();
    }

    /// Plain text, with #tags and @due dates drawn as pills.
    static void Literal(InlineCollection to, string text, Ctx ctx)
    {
        var at = 0;
        foreach (Match m in TagOrDue().Matches(text))
        {
            if (m.Index > at) to.Add(new Run(text[at..m.Index]));
            at = m.Index + m.Length;
            if (m.Groups[1].Success)
            {
                var name = m.Groups[1].Value;
                var pill = Pill("#" + name, R("Accent"), $"Show only #{name}");
                pill.MouseLeftButtonDown += (_, e) => { e.Handled = true; ctx.Tag?.Invoke(name.ToLowerInvariant()); };
                to.Add(new InlineUIContainer(pill) { BaselineAlignment = BaselineAlignment.Center });
            }
            else
            {
                var due = DateTime.ParseExact(m.Groups[2].Value, "yyyy-MM-dd HH:mm", null);
                var late = due < DateTime.Now;
                to.Add(new InlineUIContainer(Pill("⏰ " + Due.Format(due, DateTime.Now), R(late ? "Red" : "Sub"), late ? "Overdue" : "Due"))
                    { BaselineAlignment = BaselineAlignment.Center });
            }
        }
        if (at < text.Length) to.Add(new Run(text[at..]));
    }

    static Border Pill(string text, Brush fg, string tip) => new()
    {
        CornerRadius = new(8), Background = R("PillBg"), Padding = new(6, 0, 6, 1), Margin = new(1, 0, 1, 0), Cursor = Cursors.Hand, ToolTip = tip,
        Child = new TextBlock { Text = text, Foreground = fg, FontSize = 12 },
    };

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

    // ---- > [!kind] Title   (Obsidian callouts; "-" after the kind starts it folded, "+" foldable but open) ----

    static (string Glyph, Color Color) CalloutLook(string kind) => kind.ToLowerInvariant() switch
    {
        "tip" or "hint" or "important" => ("", Color.FromRgb(0x2C, 0xC7, 0xC7)),
        "success" or "check" or "done" => ("", Color.FromRgb(0x4C, 0xC3, 0x7A)),
        "question" or "help" or "faq" => ("", Color.FromRgb(0xF5, 0xB8, 0x3D)),
        "warning" or "caution" or "attention" => ("", Color.FromRgb(0xE6, 0xA6, 0x5D)),
        "failure" or "fail" or "missing" or "danger" or "error" or "bug" => ("", Color.FromRgb(0xFF, 0x5A, 0x5A)),
        "example" => ("", Color.FromRgb(0xB0, 0x7C, 0xFF)),
        "quote" or "cite" => ("", Color.FromRgb(0x9C, 0x9C, 0xA3)),
        _ => ("", Color.FromRgb(0x5A, 0xA9, 0xFF)), // note, info, abstract, todo…
    };

    static FrameworkElement? Callout(QuoteBlock q, Ctx ctx)
    {
        var start = Math.Clamp(q.Span.Start, 0, ctx.Source.Length);
        var end = ctx.Source.IndexOfAny(['\r', '\n'], start);
        var head = CalloutHead().Match(ctx.Source[start..(end < 0 ? ctx.Source.Length : end)]);
        if (!head.Success) return null;
        var kind = head.Groups[1].Value;
        var (glyph, color) = CalloutLook(kind);
        var brush = new SolidColorBrush(color);
        var title = head.Groups[3].Value.Trim() is { Length: > 0 } t ? t : char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant();

        var body = new StackPanel();
        foreach (var b in q)
        {
            FrameworkElement? e = b == q[0] && b is ParagraphBlock p ? Text(p.Inline, ctx, skipFirstLine: true) : Draw(b, ctx);
            if (e is TextBlock { Inlines.Count: 0 }) continue; // the header line was all there was
            e.Margin = new(0, 6, 0, 0);
            body.Children.Add(e);
        }
        var fold = head.Groups[2].Value;
        if (fold == "-") body.Visibility = Visibility.Collapsed;
        var chevron = new TextBlock
        {
            Text = fold == "-" ? "" : "", FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 10,
            Foreground = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0),
            Visibility = fold == "" ? Visibility.Collapsed : Visibility.Visible,
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Background = Brushes.Transparent,
            Children =
            {
                new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], Foreground = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) },
                new TextBlock { Text = title, Foreground = brush, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                chevron,
            },
        };
        if (fold != "")
        {
            header.Cursor = Cursors.Hand;
            header.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                var open = body.Visibility != Visibility.Visible;
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? "" : "";
            };
        }
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1E, color.R, color.G, color.B)), BorderBrush = brush, BorderThickness = new(2, 0, 0, 0),
            CornerRadius = new(6), Padding = new(10, 7, 10, 8), Child = new StackPanel { Children = { header, body } },
        };
    }

    static StackPanel DrawList(ListBlock l, Ctx ctx)
    {
        var panel = new StackPanel();
        var n = int.TryParse(l.OrderedStart, out var start) ? start : 1;
        foreach (var item in l.OfType<ListItemBlock>())
        {
            // Number this item's box before drawing its content, which may hold nested tasks (document order).
            var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList;
            var ordinal = task is null ? -1 : ctx.Tasks++;
            var content = Blocks(item, ctx);
            FrameworkElement mark;
            if (task is not null)
            {
                var box = new CheckBox
                {
                    Style = (Style)Application.Current.Resources["Check"], IsChecked = task.Checked, VerticalAlignment = VerticalAlignment.Top,
                    LayoutTransform = new ScaleTransform(0.8, 0.8), Margin = new(0, 1, 8, 0),
                };
                content.Opacity = task.Checked ? 0.55 : 1;
                box.Click += (_, _) => { content.Opacity = box.IsChecked == true ? 0.55 : 1; ctx.Toggle(ordinal); };
                mark = box;
            }
            else mark = new TextBlock { Text = l.IsOrdered ? $"{n}." : "•", Foreground = R("Sub"), Margin = new(0, 0, 8, 0) };
            n++;
            DockPanel.SetDock(mark, Dock.Left);
            panel.Children.Add(new DockPanel { Margin = new(0, 0, 0, 3), Children = { mark, content } });
        }
        return panel;
    }

    static Grid DrawTable(Table t, Ctx ctx)
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
                var e = Blocks(cell, ctx);
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
