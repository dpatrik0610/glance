using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// The Kanban layout: the board's nodes as cards in columns (BoardItem.Column), dragged between them.
public partial class BoardPage
{
    const double ColumnWidth = 270;

    string ColumnOf(BoardItem it) => board.Columns.Contains(it.Column) ? it.Column : board.Columns.FirstOrDefault() ?? "";

    /// Draws every column and card again. focus: a new card that opens for typing.
    void DrawKanban(BoardItem? focus = null)
    {
        foreach (var it in cards.Keys.ToList()) Detach(it);
        Columns.Children.Clear();
        if (board.Columns.Count == 0) board.Columns.Add("To do");
        foreach (var name in board.Columns) Columns.Children.Add(Column(name, focus));

        var add = new Button { Style = (Style)FindResource("Ghost"), VerticalAlignment = VerticalAlignment.Top, Margin = new(6, 0, 0, 0), ToolTip = "Add a column" };
        add.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = "Column" } },
        };
        add.Click += (_, _) =>
        {
            var n = 1;
            while (board.Columns.Contains($"Column {n}")) n++;
            board.Columns.Add($"Column {n}");
            DrawKanban();
            Changed();
        };
        Columns.Children.Add(add);
        ApplyTag();
    }

    FrameworkElement Column(string name, BoardItem? focus)
    {
        var items = board.Items.Where(i => ColumnOf(i) == name).ToList();
        var list = new StackPanel { MinHeight = 40, Background = Brushes.Transparent };
        foreach (var it in items) list.Children.Add(KanbanCard(it, it == focus));

        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.IBeam, ToolTip = "Double-click to rename" };
        var count = new TextBlock { Text = items.Count.ToString(), Style = (Style)FindResource("Label"), Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var more = IconButton("", "Column options", () => { });
        more.Click += (_, _) => Open(ColumnMenu(name, title), more);
        DockPanel.SetDock(more, Dock.Right);
        var head = new DockPanel { Margin = new(4, 0, 0, 8), Children = { more, new StackPanel { Orientation = Orientation.Horizontal, Children = { title, count } } } };
        title.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { RenameColumn(name, title); e.Handled = true; } };

        var addCard = new Button { Style = (Style)FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 4, 0, 0) };
        addCard.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = "Card" } },
        };
        addCard.Click += (_, _) => Add(new() { Column = name, W = ColumnWidth - 30 }, new Point(), focus: true);

        var column = new Border
        {
            Width = ColumnWidth, Margin = new(0, 0, 10, 0), Padding = new(10), CornerRadius = new(14), VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)FindResource("CardBg"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1), AllowDrop = true,
            Child = new DockPanel
            {
                Children = { Docked(head, Dock.Top), Docked(addCard, Dock.Bottom), new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list } },
            },
        };
        // Dropping a card: it goes before the first card whose middle is below the pointer.
        column.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(BoardItem)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        column.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(BoardItem)) is not BoardItem moved) return;
            var before = list.Children.OfType<FrameworkElement>()
                .FirstOrDefault(c => c.Tag is BoardItem o && o != moved && e.GetPosition(c).Y < c.ActualHeight / 2)?.Tag as BoardItem;
            board.Items.Remove(moved);
            moved.Column = name;
            var at = before is null ? (board.Items.FindLastIndex(i => ColumnOf(i) == name) + 1 is var n && n > 0 ? n : board.Items.Count) : board.Items.IndexOf(before);
            board.Items.Insert(at, moved);
            Dispatcher.BeginInvoke(() => DrawKanban()); // not while the drag is still unwinding
            Changed();
            e.Handled = true;
        };
        return column;
    }

    static T Docked<T>(T e, Dock dock) where T : UIElement
    {
        DockPanel.SetDock(e, dock);
        return e;
    }

    Border KanbanCard(BoardItem it, bool focus)
    {
        var (body, edit, cleanup) = Build(it, focus);
        if (cleanup is not null) cleanups[it] = cleanup;
        var remove = new Button
        {
            Style = (Style)FindResource("Ghost"), Content = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10,
            Padding = new(5), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Remove", Opacity = 0,
        };
        remove.Click += (_, _) => Remove(it);
        var card = new Border
        {
            Background = (Brush)FindResource("Popup"), BorderBrush = Edge(it), BorderThickness = new(1), CornerRadius = new(10),
            Padding = new(10, 0, 10, 10), Margin = new(0, 0, 0, 8), Tag = it, Cursor = Cursors.Hand,
            Child = new DockPanel { Children = { Docked(new Grid { Height = 20, Children = { remove } }, Dock.Top), body } },
        };
        card.MouseEnter += (_, _) => Fade(remove, 1);
        card.MouseLeave += (_, _) => Fade(remove, 0);
        // Press and move a few pixels to drag it to another place or column; double-click edits.
        Point? down = null;
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && edit is not null) { edit(true); e.Handled = true; return; }
            down = e.GetPosition(card);
        };
        card.MouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) { down = null; return; }
            if ((e.GetPosition(card) - d).Length < 6) return;
            down = null;
            card.Opacity = 0.4;
            DragDrop.DoDragDrop(card, new DataObject(typeof(BoardItem), it), DragDropEffects.Move);
            card.Opacity = 1;
        };
        NodeMenuOn(card, it, edit);
        cards[it] = card;
        return card;
    }

    ContextMenu ColumnMenu(string name, TextBlock title)
    {
        var m = new ContextMenu();
        var at = board.Columns.IndexOf(name);
        Item(m, "Rename", () => RenameColumn(name, title));
        Item(m, "Move left", () => MoveColumn(name, -1), at > 0);
        Item(m, "Move right", () => MoveColumn(name, 1), at < board.Columns.Count - 1);
        Item(m, "Delete column", () => DeleteColumn(name), board.Columns.Count > 1);
        return m;
    }

    void RenameColumn(string name, TextBlock title)
    {
        var box = new TextBox { Text = name, MinWidth = 140, Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent, FontWeight = FontWeights.SemiBold };
        var panel = (Panel)title.Parent;
        var at = panel.Children.IndexOf(title);
        panel.Children.RemoveAt(at);
        panel.Children.Insert(at, box);
        var done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            var to = box.Text.Trim();
            if (keep && to.Length > 0 && to != name && !board.Columns.Contains(to))
            {
                board.Columns[board.Columns.IndexOf(name)] = to;
                foreach (var it in board.Items.Where(i => i.Column == name)) it.Column = to;
                Changed();
            }
            Dispatcher.BeginInvoke(() => DrawKanban());
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Finish(e.Key == Key.Enter); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        FocusWhenVisible(box);
        box.SelectAll();
    }

    void MoveColumn(string name, int by)
    {
        var i = board.Columns.IndexOf(name);
        board.Columns.RemoveAt(i);
        board.Columns.Insert(i + by, name);
        DrawKanban();
        Changed();
    }

    /// Its cards move to the column before it (or the next one, for the first).
    void DeleteColumn(string name)
    {
        var i = board.Columns.IndexOf(name);
        var to = board.Columns[i == 0 ? 1 : i - 1];
        var moving = board.Items.Where(it => ColumnOf(it) == name).ToList();
        if (moving.Count > 0 && !Dialog.Confirm("Delete column", $"Delete \"{name}\"?", $"Its {moving.Count} card{(moving.Count == 1 ? "" : "s")} move to \"{to}\".", "Delete")) return;
        foreach (var it in moving) it.Column = to;
        board.Columns.RemoveAt(i);
        DrawKanban();
        Changed();
    }
}
