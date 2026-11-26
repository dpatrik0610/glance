using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// Every open task in one list: checklist items from Notes and Stickies, and "- [ ]" lines in board notes,
/// grouped by when they're due. Ticking one ticks it where it lives; the source name jumps there.
public class TasksPage : UserControl, IHeader
{
    /// A task from either place, with how to tick it and how to go to it.
    record Row(string Text, DateTime? Due, string Source, Action Tick, Action Go);

    readonly StackPanel list = new();
    readonly CheckBox showDone;
    bool withDone;

    public FrameworkElement Header => showDone;
    public FrameworkElement State { get; } = new TextBlock { Style = Skin.Style("Label") };

    public TasksPage()
    {
        showDone = new CheckBox { Content = "Show done  ", Style = Skin.Style("Switch"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        showDone.Click += (_, _) => { withDone = showDone.IsChecked == true; Draw(); };
        var hint = new TextBlock { Text = "From Notes, Stickies and board notes. A due date on a board task: - [ ] call mom @tomorrow 9", Style = Skin.Style("Label"), Margin = new(12, 4, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top);
        Content = new DockPanel { Children = { hint, new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Padding = new(12, 0, 8, 0) } } };
        IsVisibleChanged += (_, _) => { if (IsVisible) Draw(); };
        Draw();
    }

    IEnumerable<(Row Row, bool Done)> All()
    {
        foreach (var n in NoteStore.Everything)
            foreach (var i in n.Items)
                yield return (new Row(i.Text, i.Due, n is Sticky ? $"Sticky · {n.DisplayTitle}" : n.DisplayTitle, () => i.Done = !i.Done, () =>
                {
                    if (n is Sticky s) { Stickies.Reveal(s); return; }
                    App.Go("Notes");
                    NoteStore.RequestFocus(n);
                }), i.Done);
        foreach (var t in BoardStore.Tasks())
            yield return (new Row(t.Text, t.Due, $"{t.Board.Name} · {(t.Item.Title.Trim() is { Length: > 0 } title ? title : "note")}", () =>
            {
                t.Item.Text = MarkdownView.Toggle(t.Item.Text, t.Ordinal);
                BoardStore.Changed(t.Item);
            }, () => ((BoardPage)App.Go("Board")).Reveal(t.Board, t.Item)), t.Done);
    }

    void Draw()
    {
        list.Children.Clear();
        var now = DateTime.Now;
        var every = All().ToList();
        var rows = every.Where(r => withDone || !r.Done).ToList();
        var open = every.Count(r => !r.Done);
        var late = every.Count(r => !r.Done && r.Row.Due < now);
        ((TextBlock)State).Text = late > 0 ? $"{open} open · {late} overdue" : $"{open} open";
        string Group((Row Row, bool Done) r) => r.Done ? "Done" : r.Row.Due is not { } d ? "No date" : d < now ? "Overdue" : d.Date == now.Date ? "Today" : "Upcoming";
        string[] order = ["Overdue", "Today", "Upcoming", "No date", "Done"];
        foreach (var g in rows.GroupBy(Group).OrderBy(g => Array.IndexOf(order, g.Key)))
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{g.Key}  {g.Count()}", Style = Skin.Style("Label"), FontWeight = FontWeights.SemiBold, Margin = new(2, list.Children.Count == 0 ? 0 : 16, 0, 6),
                Foreground = Skin.Brush(g.Key == "Overdue" ? "Red" : "Sub"),
            });
            foreach (var (row, done) in g.OrderBy(r => r.Row.Due ?? DateTime.MaxValue)) list.Children.Add(Line(row, done, g.Key == "Overdue"));
        }
        if (rows.Count == 0)
            list.Children.Add(new TextBlock { Text = "Nothing to do. Add a checklist in Notes, or a - [ ] task to a board note.", Style = Skin.Style("Label"), Margin = new(2, 8, 0, 0) });
    }

    FrameworkElement Line(Row row, bool done, bool overdue)
    {
        var box = new CheckBox { Style = Skin.Style("Check"), IsChecked = done, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 10, 0) };
        box.Click += (_, _) => { row.Tick(); Draw(); };
        var source = new Button { Style = Skin.Style("Ghost"), Content = row.Source, FontSize = 12, Padding = new(6, 2, 6, 2), ToolTip = "Go to it", VerticalAlignment = VerticalAlignment.Top };
        source.Click += (_, _) => row.Go();
        var text = new TextBlock { Text = row.Text.Length > 0 ? row.Text : "(no text)", TextWrapping = TextWrapping.Wrap, Opacity = done ? 0.55 : 1, VerticalAlignment = VerticalAlignment.Center };
        if (done) text.TextDecorations = TextDecorations.Strikethrough;
        if (row.Due is { } d)
            text.Inlines.Add(new System.Windows.Documents.Run("   " + Due.Format(d, DateTime.Now)) { FontSize = 12, Foreground = Skin.Brush(overdue ? "Red" : "Sub") });
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(source, Dock.Right);
        return new DockPanel { Margin = new(2, 0, 0, 6), Children = { box, source, text } };
    }
}
