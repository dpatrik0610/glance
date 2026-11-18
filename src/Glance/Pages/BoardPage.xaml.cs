using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// An endless dotted canvas of nodes (BoardPage.Nodes.cs). Board units are pixels at 100%; the view is pan (screen px) + zoom.
// ponytail: prototype. No multi-select, lines between nodes or undo yet.
public partial class BoardPage : UserControl
{
    const double Tile = 24, MinW = 120, MaxW = 1200, MinZoom = 0.25, MaxZoom = 3;
    static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    readonly Boards boards = BoardStore.Load();
    Board board;
    readonly DispatcherTimer saveSoon = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<BoardItem, Border> cards = [];
    readonly Dictionary<BoardItem, Action> cleanups = [];
    /// Once a second, for timers.
    event Action? Tick;
    Point? panFrom;
    (BoardItem Item, Border Card, Vector Grab)? dragging;
    (BoardItem Item, FrameworkElement Inner, double FromX, double FromW)? resizing;
    // The minimap's board → map mapping; held still while it's being dragged so the map doesn't slide under the pointer.
    double mapScale = 1;
    Vector mapOffset;
    bool mapDragging;

    public BoardPage()
    {
        InitializeComponent();
        saveSoon.Tick += (_, _) => Save();
        Application.Current.Exit += (_, _) => { if (saveSoon.IsEnabled) Save(); };
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick?.Invoke(), Dispatcher).Start();
        // The card's corners are round; keep the dots inside them.
        Surface.SizeChanged += (_, _) =>
        {
            Surface.Clip = new RectangleGeometry(new Rect(Surface.RenderSize), 17, 17);
            DrawMap();
        };
        board = boards.All.FirstOrDefault(b => b.Id == boards.Current) ?? boards.All[0];
        foreach (var it in board.Items) Show(it);
        DrawTabs();
        ApplyView();
    }

    void Save()
    {
        saveSoon.Stop();
        BoardStore.Save(boards);
    }

    // ---- boards: one tab each ----

    void DrawTabs()
    {
        TabStrip.Children.Clear();
        foreach (var b in boards.All)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"), GroupName = "boards", Content = b.Name, IsChecked = b == board,
                ToolTip = "Double-click to rename, right-click for more",
            };
            chip.Checked += (_, _) => Switch(b);
            chip.MouseDoubleClick += (_, e) => { Rename(b, chip); e.Handled = true; };
            var menu = new ContextMenu();
            void Item(string header, Action act, bool enabled = true)
            {
                var i = new MenuItem { Header = header, IsEnabled = enabled };
                i.Click += (_, _) => act();
                menu.Items.Add(i);
            }
            var at = boards.All.IndexOf(b);
            Item("Rename", () => Rename(b, chip));
            Item("Move left", () => MoveBoard(b, -1), at > 0);
            Item("Move right", () => MoveBoard(b, 1), at < boards.All.Count - 1);
            Item("Delete", () => DeleteBoard(b));
            chip.ContextMenu = menu;
            TabStrip.Children.Add(chip);
        }
    }

    void Switch(Board b)
    {
        if (b == board) return;
        (panFrom, dragging, resizing) = (null, null, null);
        foreach (var it in cards.Keys.ToList()) Drop(it);
        board = b;
        boards.Current = b.Id;
        foreach (var it in board.Items) Show(it);
        ApplyView();
        Changed();
    }

    void NewBoard(object sender, RoutedEventArgs e)
    {
        var b = new Board { Name = $"Board {boards.All.Count + 1}" };
        boards.All.Add(b);
        Switch(b);
        DrawTabs();
        Rename(b, (RadioButton)TabStrip.Children[^1]);
    }

    /// The tab's name turns into a box; Enter or clicking away keeps it, Esc doesn't.
    void Rename(Board b, RadioButton chip)
    {
        var box = new TextBox { Text = b.Name, MinWidth = 90, Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent };
        var done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            if (keep && box.Text.Trim() is { Length: > 0 } name) b.Name = name;
            chip.Content = b.Name;
            Changed();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Finish(e.Key == Key.Enter); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        chip.Content = box;
        FocusWhenVisible(box);
        box.SelectAll();
    }

    void MoveBoard(Board b, int by)
    {
        var i = boards.All.IndexOf(b);
        boards.All.RemoveAt(i);
        boards.All.Insert(i + by, b);
        DrawTabs();
        Changed();
    }

    void DeleteBoard(Board b)
    {
        if (b.Items.Count > 0 && !Dialog.Confirm("Delete board", $"Delete \"{b.Name}\"?",
                $"Its {b.Items.Count} node{(b.Items.Count == 1 ? "" : "s")} go with it. This can't be undone.", "Delete")) return;
        var i = boards.All.IndexOf(b);
        if (b == board)
        {
            if (boards.All.Count == 1) boards.All.Add(new());
            Switch(boards.All[i == 0 ? 1 : i - 1]);
        }
        boards.All.Remove(b);
        foreach (var it in b.Items.Where(it => it.File is not null))
            try { File.Delete(System.IO.Path.Combine(BoardStore.Files, it.File!)); } catch (IOException) { }
        DrawTabs();
        Changed();
    }

    void Changed()
    {
        saveSoon.Stop();
        saveSoon.Start();
        Hint.Visibility = board.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawMap();
    }

    // ---- view: pan, zoom, fit ----

    void ApplyView()
    {
        World.RenderTransform = new MatrixTransform(board.Zoom, 0, 0, board.Zoom, board.X, board.Y);
        Dots.Viewport = new Rect(board.X, board.Y, Tile * board.Zoom, Tile * board.Zoom);
        ZoomText.Content = $"{board.Zoom:P0}";
        Hint.Visibility = board.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawMap();
    }

    Point ToBoard(Point screen) => new((screen.X - board.X) / board.Zoom, (screen.Y - board.Y) / board.Zoom);

    Point ScreenMiddle => new(Surface.ActualWidth / 2, Surface.ActualHeight / 2);

    /// Where new things go when there's no pointer position: the middle of the view.
    Point Middle => ToBoard(ScreenMiddle) - new Vector(110, 40);

    /// Zooms keeping the board point under `screen` where it is.
    void ZoomAt(Point screen, double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var f = zoom / board.Zoom;
        board.X = screen.X - (screen.X - board.X) * f;
        board.Y = screen.Y - (screen.Y - board.Y) * f;
        board.Zoom = zoom;
        ApplyView();
        Changed();
    }

    void Wheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(Surface), board.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    void ZoomIn(object sender, RoutedEventArgs e) => ZoomAt(ScreenMiddle, board.Zoom * 1.25);
    void ZoomOut(object sender, RoutedEventArgs e) => ZoomAt(ScreenMiddle, board.Zoom / 1.25);
    void ZoomReset(object sender, RoutedEventArgs e) => ZoomAt(ScreenMiddle, 1);

    /// Every node's box, in board units.
    Rect Bounds(BoardItem it) => cards[it] is { ActualWidth: > 0 } c ? new(it.X, it.Y, c.ActualWidth, c.ActualHeight) : new(it.X, it.Y, it.W, 80);

    /// Zooms and centers so every node is in view (not past 150%).
    void Fit(object sender, RoutedEventArgs e)
    {
        if (board.Items.Count == 0) { (board.X, board.Y, board.Zoom) = (40, 40, 1); ApplyView(); Changed(); return; }
        var all = board.Items.Select(Bounds).Aggregate(Rect.Union);
        const double pad = 60;
        board.Zoom = Math.Clamp(Math.Min((Surface.ActualWidth - 2 * pad) / all.Width, (Surface.ActualHeight - 2 * pad) / all.Height), MinZoom, 1.5);
        board.X = Surface.ActualWidth / 2 - (all.X + all.Width / 2) * board.Zoom;
        board.Y = Surface.ActualHeight / 2 - (all.Y + all.Height / 2) * board.Zoom;
        ApplyView();
        Changed();
    }

    // ---- minimap ----

    void DrawMap()
    {
        double w = Map.ActualWidth, h = Map.ActualHeight;
        if (w <= 0 || h <= 0 || Surface.ActualWidth <= 0) return;
        var view = new Rect(ToBoard(new Point(0, 0)), ToBoard(new Point(Surface.ActualWidth, Surface.ActualHeight)));
        if (!mapDragging)
        {
            var all = board.Items.Select(Bounds).Aggregate(view, Rect.Union);
            mapScale = Math.Min(w / all.Width, h / all.Height);
            mapOffset = new Vector((w - all.Width * mapScale) / 2 - all.X * mapScale, (h - all.Height * mapScale) / 2 - all.Y * mapScale);
        }
        Map.Children.Clear();
        var node = (Brush)FindResource("Sub");
        foreach (var it in board.Items) Map.Children.Add(MapRect(Bounds(it), node, null, 0.55));
        Map.Children.Add(MapRect(view, null, (Brush)FindResource("Accent"), 1));
    }

    Rectangle MapRect(Rect r, Brush? fill, Brush? stroke, double opacity)
    {
        var e = new Rectangle
        {
            Width = Math.Max(2, r.Width * mapScale), Height = Math.Max(2, r.Height * mapScale), Fill = fill, Stroke = stroke,
            StrokeThickness = 1.5, RadiusX = 2, RadiusY = 2, Opacity = opacity, IsHitTestVisible = false,
        };
        Canvas.SetLeft(e, r.X * mapScale + mapOffset.X);
        Canvas.SetTop(e, r.Y * mapScale + mapOffset.Y);
        return e;
    }

    /// Centers the view on the board point under a minimap position.
    void MapGo(Point m)
    {
        var b = (Vector)(m - mapOffset) / mapScale;
        board.X = Surface.ActualWidth / 2 - b.X * board.Zoom;
        board.Y = Surface.ActualHeight / 2 - b.Y * board.Zoom;
        ApplyView();
    }

    void MapDown(object sender, MouseButtonEventArgs e)
    {
        mapDragging = true;
        MapBox.CaptureMouse();
        MapGo(e.GetPosition(Map));
        e.Handled = true;
    }

    void MapMove(object sender, MouseEventArgs e)
    {
        if (mapDragging) MapGo(e.GetPosition(Map));
    }

    void MapUp(object sender, MouseButtonEventArgs e)
    {
        if (!mapDragging) return;
        mapDragging = false;
        MapBox.ReleaseMouseCapture();
        Changed();
        e.Handled = true;
    }

    // ---- pointer on the background: pan, add ----

    // Left or middle drag on the background pans; a left double-click adds a note, a right click opens the Add menu there.
    // Nodes and their controls handle their own clicks, so only the background (and middle drags anywhere) get here.
    void SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        Focus(); // for Ctrl+V; also takes focus out of a node being edited
        var p = e.GetPosition(Surface);
        if (e.ChangedButton == MouseButton.Right) { AddMenu(ToBoard(p)).IsOpen = true; return; }
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { NewNote(ToBoard(p), ""); return; }
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        panFrom = p;
        Surface.CaptureMouse();
        Surface.Cursor = Cursors.SizeAll;
    }

    void SurfaceMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        if (resizing is { } r)
        {
            r.Item.W = Math.Clamp(r.FromW + ToBoard(p).X - r.FromX, MinW, MaxW);
            r.Inner.Width = r.Item.W;
        }
        else if (dragging is { } d)
        {
            var at = ToBoard(p) - d.Grab;
            (d.Item.X, d.Item.Y) = (at.X, at.Y);
            Place(d.Item, d.Card);
            DrawMap();
        }
        else if (panFrom is { } from)
        {
            board.X += p.X - from.X;
            board.Y += p.Y - from.Y;
            panFrom = p;
            ApplyView();
        }
    }

    void SurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (panFrom is null && dragging is null && resizing is null) return;
        panFrom = null;
        dragging = null;
        resizing = null;
        Surface.ReleaseMouseCapture();
        Surface.Cursor = null;
        Changed();
    }

    void AddClick(object sender, RoutedEventArgs e)
    {
        var menu = AddMenu(Middle);
        menu.PlacementTarget = AddButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    ContextMenu AddMenu(Point at)
    {
        var menu = new ContextMenu();
        void Item(string header, Action add)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => add();
            menu.Items.Add(i);
        }
        Item("Note", () => NewNote(at, ""));
        Item("Link (website, file or folder)", () => Add(new() { Kind = "link", W = 260 }, at, focus: true));
        Item("Timer", () => Add(new() { Kind = "timer" }, at));
        Item("Live stat (CPU, GPU, RAM, network)", () => Add(new() { Kind = "stat", Text = Metrics[0] }, at));
        Item("Picture from a file…", () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|" + string.Join(";", Pictures.Select(x => "*" + x)), Multiselect = true };
            if (pick.ShowDialog() == true) AddFiles(pick.FileNames, at);
        });
        return menu;
    }

    // ---- nodes: the card around each one (drag, resize, remove) ----

    void Add(BoardItem it, Point at, bool focus = false)
    {
        (it.X, it.Y) = (at.X, at.Y);
        board.Items.Add(it);
        Show(it, focus);
        Changed();
    }

    void NewNote(Point at, string text) => Add(new() { Text = text }, at, focus: text == "");

    void NewPicture(Point at, BitmapSource? image = null, string? copyFrom = null)
    {
        var it = new BoardItem { Kind = "image" };
        Directory.CreateDirectory(BoardStore.Files);
        it.File = it.Id + (copyFrom is null ? ".png" : System.IO.Path.GetExtension(copyFrom).ToLowerInvariant());
        var path = System.IO.Path.Combine(BoardStore.Files, it.File);
        try
        {
            if (copyFrom is not null) File.Copy(copyFrom, path);
            else
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(image!));
                using var f = File.Create(path);
                png.Save(f);
            }
        }
        catch (IOException) { return; }
        it.W = LoadPicture(it) is { } b ? Math.Clamp(b.Width, MinW, 320) : 320; // small pictures stay small
        Add(it, at);
    }

    void Show(BoardItem it, bool focus = false)
    {
        var (body, edit, cleanup) = Build(it, focus);
        if (cleanup is not null) cleanups[it] = cleanup;

        var remove = new Button
        {
            Style = (Style)FindResource("Ghost"), Content = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10,
            Padding = new(5), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Remove", Opacity = 0,
        };
        var head = new Grid { Height = 22, Children = { remove } };
        DockPanel.SetDock(head, Dock.Top);
        var inner = new DockPanel { Width = it.W, Children = { head, body } };
        // Resize: drag the bottom-right corner (width; the height follows the content).
        var grip = new Border
        {
            Width = 16, Height = 16, Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, -10, -10),
            ToolTip = "Drag to resize",
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M10,2 L2,10 M10,6 L6,10"), Stroke = (Brush)FindResource("Sub"), StrokeThickness = 1.3 },
        };
        var card = new Border
        {
            Background = (Brush)FindResource("Popup"), BorderBrush = (Brush)FindResource("CardEdge"), BorderThickness = new(1),
            CornerRadius = new(12), Padding = new(12, 2, 12, 12), Cursor = Cursors.SizeAll,
            Child = new Grid { Children = { inner, grip } },
        };
        card.MouseEnter += (_, _) => remove.Opacity = grip.Opacity = 1;
        card.MouseLeave += (_, _) => remove.Opacity = grip.Opacity = 0;
        card.SizeChanged += (_, _) => DrawMap();
        remove.Click += (_, _) => Remove(it);
        grip.MouseLeftButtonDown += (_, e) =>
        {
            resizing = (it, inner, ToBoard(e.GetPosition(Surface)).X, it.W);
            Surface.CaptureMouse();
            e.Handled = true;
        };
        // Drag by anything that doesn't keep its own clicks (text boxes, buttons, task boxes, links); double-click edits.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && edit is not null) { edit(true); e.Handled = true; return; }
            board.Items.Remove(it); // to the end of the list and the top of the stack
            board.Items.Add(it);
            World.Children.Remove(card);
            World.Children.Add(card);
            dragging = (it, card, ToBoard(e.GetPosition(Surface)) - new Point(it.X, it.Y));
            Surface.CaptureMouse();
            e.Handled = true;
        };
        cards[it] = card;
        Place(it, card);
        World.Children.Add(card);
    }

    /// Builds a node again in place (after its settings changed).
    void Rebuild(BoardItem it)
    {
        Drop(it);
        Show(it);
        Changed();
    }

    /// Takes a node's card off the board and stops what it was doing (the item itself stays).
    void Drop(BoardItem it)
    {
        if (cleanups.Remove(it, out var stop)) stop();
        World.Children.Remove(cards[it]);
        cards.Remove(it);
    }

    void Remove(BoardItem it)
    {
        if (!board.Items.Contains(it)) return; // already gone (an emptied node removes itself as focus leaves)
        if (it.Kind == "note" && (it.Title + it.Text).Trim().Length > 0
            && !Dialog.Confirm("Remove note", "Remove this note from the board?", "This can't be undone.", "Remove")) return;
        board.Items.Remove(it);
        Drop(it);
        if (it.File is not null) try { File.Delete(System.IO.Path.Combine(BoardStore.Files, it.File)); } catch (IOException) { }
        Changed();
    }

    /// Focus can't land on an element until it's shown, which for a new card or a just-switched editor is after layout.
    static void FocusWhenVisible(UIElement e)
    {
        if (e.IsVisible) { e.Focus(); return; }
        DependencyPropertyChangedEventHandler? once = null;
        once = (_, _) =>
        {
            if (!e.IsVisible) return;
            e.IsVisibleChanged -= once;
            e.Focus();
        };
        e.IsVisibleChanged += once;
    }

    static void Place(BoardItem it, FrameworkElement card)
    {
        Canvas.SetLeft(card, it.X);
        Canvas.SetTop(card, it.Y);
    }

    static BitmapImage? LoadPicture(BoardItem it)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(System.IO.Path.Combine(BoardStore.Files, it.File!));
            b.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    // ---- paste and drop ----

    void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || e.OriginalSource is TextBox) return;
        var at = Surface.IsMouseOver ? ToBoard(Mouse.GetPosition(Surface)) : Middle;
        if (Clipboard.ContainsFileDropList()) AddFiles(Clipboard.GetFileDropList().Cast<string>(), at);
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) NewPicture(at, img);
        else if (Clipboard.ContainsText()) AddText(Clipboard.GetText(), at);
        else return;
        e.Handled = true;
    }

    void Dropped(object sender, DragEventArgs e)
    {
        var at = ToBoard(e.GetPosition(Surface));
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files, at);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text) AddText(text, at);
    }

    /// A lone web address becomes a link; anything else a note.
    void AddText(string text, Point at)
    {
        var t = text.Trim();
        if (!t.Contains('\n') && Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Add(new() { Kind = "link", W = 260, Text = t }, at);
        else NewNote(at, text);
    }

    /// Pictures become pictures; other files and folders become links.
    void AddFiles(IEnumerable<string> files, Point at)
    {
        foreach (var f in files)
        {
            if (Pictures.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())) NewPicture(at, copyFrom: f);
            else Add(new() { Kind = "link", W = 260, Text = f }, at);
            at.Offset(24, 24);
        }
    }
}
