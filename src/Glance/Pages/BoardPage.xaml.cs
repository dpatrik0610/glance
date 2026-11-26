using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Glance.Services;
using IOPath = System.IO.Path;

namespace Glance.Pages;

/// Boards (one tab each) of nodes: notes, pictures, links, groups, live stats, embedded notes; with arrows between them and a pen layer.
/// Canvas layout here; Kanban, node bodies, menus and arrows/ink are the other BoardPage.*.cs parts.
/// Board units are pixels at 100%; the view is pan (screen px) + zoom. Everything lives in BoardStore.Data and saves itself.
public partial class BoardPage : UserControl, IHeader
{
    public FrameworkElement Header { get; }

    const double Tile = 24, MinW = 120, MaxW = 1600, MinZoom = 0.25, MaxZoom = 3, Pad = 26; // Pad: a card's padding + border, around W
    static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    readonly Boards boards = BoardStore.Data;
    Board board;
    readonly Dictionary<BoardItem, Border> cards = [];
    readonly Dictionary<BoardItem, Action> cleanups = [];
    Point? panFrom;
    (BoardItem Item, Border Card, Vector Grab, List<(BoardItem Item, Border Card, Vector Offset)> Carry)? dragging;
    (BoardItem Item, Border Card, FrameworkElement Inner, Point From, double W, double H)? resizing;
    (BoardItem From, Line Rubber)? connecting;
    // The minimap's board → map mapping; held still while it's being dragged so the map doesn't slide under the pointer.
    double mapScale = 1;
    Vector mapOffset;
    bool mapDragging;
    /// Only nodes with this #tag are shown at full strength (null: all).
    string? tagFilter;

    bool Kanban => board.Layout == "kanban";

    public BoardPage()
    {
        InitializeComponent();
        Header = MainWindow.Lift(Bar);
        board = boards.All.FirstOrDefault(b => b.Id == boards.Current) ?? boards.All[0];
        // The card's corners are round; keep the dots inside them.
        Surface.SizeChanged += (_, _) =>
        {
            Surface.Clip = new RectangleGeometry(new Rect(Surface.RenderSize), 17, 17);
            DrawMap();
        };
        Ink.StrokeCollected += (_, _) => SaveInk();
        Ink.StrokeErased += (_, _) => SaveInk();
        // Ticked on the Tasks page, or added by quick capture: draw it (again).
        BoardStore.Edited += it =>
        {
            if (!board.Items.Contains(it)) return;
            if (cards.ContainsKey(it) || Kanban) Rebuild(it);
            else { Show(it); ApplyTag(); Changed(); }
        };
        DrawTabs();
        Refresh();
    }

    void Changed()
    {
        BoardStore.SaveSoon();
        Track();
        Hint.Visibility = board.Items.Count == 0 && !Kanban ? Visibility.Visible : Visibility.Collapsed;
        Redraw();
    }

    /// Everything that follows node positions and sizes.
    void Redraw()
    {
        if (Kanban) return;
        DrawEdges();
        DrawMap();
    }

    /// Draws the current board from scratch (after switching boards or layouts, or an undo).
    void Refresh(bool fade = true)
    {
        _ = HistoryOf(board); // its state before anything changes here
        (panFrom, dragging, resizing) = (null, null, null);
        foreach (var it in cards.Keys.ToList()) Detach(it);
        LoadInk();
        var kanban = Kanban;
        World.Visibility = MapBox.Visibility = ZoomBar.Visibility = PenButton.Visibility = EraserButton.Visibility
            = kanban ? Visibility.Collapsed : Visibility.Visible;
        KanbanHost.Visibility = kanban ? Visibility.Visible : Visibility.Collapsed;
        if (kanban) { SetPen(null); DrawKanban(); }
        else foreach (var it in board.Items) Show(it);
        DrawBackground();
        ApplyView();
        ApplyTag();
        Changed();
        if (!fade) return;
        // A switched-to board (or layout) fades in.
        UIElement shown = kanban ? KanbanHost : World;
        shown.Opacity = 0;
        Fade(shown, 1, 220);
    }

    // ---- boards: one tab each ----

    void DrawTabs()
    {
        TabStrip.Children.Clear();
        foreach (var b in boards.All)
        {
            var chip = new RadioButton
            {
                Style = Skin.Style("Chip"), GroupName = "boards", Content = b.Name, IsChecked = b == board,
                ToolTip = "Double-click to rename, right-click for more",
            };
            chip.Checked += (_, _) => Switch(b);
            chip.MouseDoubleClick += (_, e) => { Rename(b, chip); e.Handled = true; };
            chip.ContextMenu = TabMenu(b, chip);
            TabStrip.Children.Add(chip);
        }
    }

    void Switch(Board b)
    {
        if (b == board) return;
        Commit(); // the last changes here are one undo step on this board
        Select(null);
        board = b;
        boards.Current = b.Id;
        tagFilter = null;
        Refresh();
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
        DrawTabs();
        Changed();
    }

    /// Opens a board (and centers a node on it), from a [[link]], the Tasks page or Ctrl+K.
    public void Reveal(Board b, BoardItem? it = null)
    {
        Switch(b);
        DrawTabs();
        if (it is null || Kanban) return;
        // Right after the page is created it has no size yet: center once it's laid out.
        Dispatcher.BeginInvoke(() =>
        {
            if (!cards.ContainsKey(it)) return;
            var r = Bounds(it);
            board.X = Surface.ActualWidth / 2 - (r.X + r.Width / 2) * board.Zoom;
            board.Y = Surface.ActualHeight / 2 - (r.Y + r.Height / 2) * board.Zoom;
            ApplyView(ease: true);
            ToFront(it);
            Flash(cards[it]);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// A brief accent edge, to show which node a jump landed on.
    void Flash(Border card)
    {
        var was = card.BorderBrush;
        card.BorderBrush = Skin.Brush("Accent");
        card.BorderThickness = new(2);
        new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1.2), System.Windows.Threading.DispatcherPriority.Background,
            (s, _) => { ((System.Windows.Threading.DispatcherTimer)s!).Stop(); card.BorderBrush = was; card.BorderThickness = new(1); }, Dispatcher);
    }

    /// [[Target]]: a node titled so on this board, a board, a Notes-page note, a node on another board; else a new note by that name.
    void Go(string target)
    {
        var t = target.Trim();
        bool Is(string s) => string.Equals(s.Trim(), t, StringComparison.OrdinalIgnoreCase);
        if (board.Items.FirstOrDefault(i => Is(i.Title)) is { } here) { Reveal(board, here); return; }
        if (boards.All.FirstOrDefault(b => Is(b.Name)) is { } other) { Reveal(other); return; }
        if (NoteStore.Notes.FirstOrDefault(n => Is(n.Title)) is { } note)
        {
            App.Go("Notes");
            NoteStore.RequestFocus(note);
            return;
        }
        if (boards.All.SelectMany(b => b.Items.Select(i => (b, i))).FirstOrDefault(x => Is(x.i.Title)) is ({ } ob, { } oi)) { Reveal(ob, oi); return; }
        if (Kanban) return;
        Add(new() { Title = t }, Middle, focus: true);
    }

    // ---- #tag filter ----

    void ShowTag(string tag)
    {
        tagFilter = tagFilter == tag ? null : tag;
        ApplyTag();
    }

    void ClearTag(object sender, RoutedEventArgs e) => ShowTag(tagFilter ?? "");

    void ApplyTag()
    {
        TagButton.Visibility = tagFilter is null ? Visibility.Collapsed : Visibility.Visible;
        TagButton.Content = $"#{tagFilter}  ✕";
        foreach (var (it, card) in cards)
            Fade(card, tagFilter is null || MarkdownView.Tags(it.Title + "\n" + it.Text).Contains(tagFilter) ? 1 : 0.18, 200);
    }

    // ---- view: pan, zoom, fit, background ----

    // What's on screen. board.X/Y/Zoom is where the view is going: zooms, fits and jumps ease toward it a frame at a time;
    // panning follows the pointer at once. Zoom eases in log space (in and out feel alike); a zoom about a point keeps that point still.
    double shownX, shownY, shownZoom = 1;
    Point? easeAnchor;
    TimeSpan? lastFrame;
    bool easing;

    void ApplyView(bool ease = false, Point? anchor = null)
    {
        ZoomText.Content = $"{board.Zoom:P0}";
        easeAnchor = anchor;
        if (ease && IsVisible)
        {
            if (!easing) { easing = true; lastFrame = null; CompositionTarget.Rendering += Ease; }
            return;
        }
        StopEasing();
        (shownX, shownY, shownZoom) = (board.X, board.Y, board.Zoom);
        Render();
    }

    void StopEasing()
    {
        if (!easing) return;
        easing = false;
        CompositionTarget.Rendering -= Ease;
    }

    void Ease(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame) return; // raised more than once per frame
        var dt = lastFrame is { } last ? Math.Min((now - last).TotalSeconds, 0.05) : 1 / 60.0;
        lastFrame = now;
        var k = 1 - Math.Exp(-dt * 14); // about 90% of the way in 160 ms, whatever the frame rate
        shownZoom = Math.Exp(Math.Log(shownZoom) + (Math.Log(board.Zoom) - Math.Log(shownZoom)) * k);
        if (easeAnchor is { } a)
        {
            // The board point that ends up under the anchor stays under it all the way.
            var p = new Point((a.X - board.X) / board.Zoom, (a.Y - board.Y) / board.Zoom);
            (shownX, shownY) = (a.X - p.X * shownZoom, a.Y - p.Y * shownZoom);
        }
        else
        {
            shownX += (board.X - shownX) * k;
            shownY += (board.Y - shownY) * k;
        }
        if (Math.Abs(Math.Log(board.Zoom / shownZoom)) < 0.0005 && Math.Abs(board.X - shownX) < 0.3 && Math.Abs(board.Y - shownY) < 0.3)
        {
            StopEasing();
            (shownX, shownY, shownZoom) = (board.X, board.Y, board.Zoom);
        }
        Render();
    }

    void Render()
    {
        World.RenderTransform = new MatrixTransform(shownZoom, 0, 0, shownZoom, shownX, shownY);
        Dots.Viewport = new Rect(shownX, shownY, Tile * shownZoom, Tile * shownZoom);
        DrawMap();
    }

    /// Eases an element's opacity (or another double property) to a value; the value stays set when it's done.
    static void Fade(UIElement e, double to, double ms = 140, DependencyProperty? property = null)
    {
        property ??= OpacityProperty;
        var from = (double)e.GetValue(property);
        e.SetValue(property, to);
        e.BeginAnimation(property, new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        });
    }

    /// A deleted node shrinks and fades out, then goes.
    void Vanish(FrameworkElement card)
    {
        card.IsHitTestVisible = false;
        World.Children.Add(card);
        var scale = new ScaleTransform(1, 1);
        (card.RenderTransform, card.RenderTransformOrigin) = (scale, new(0.5, 0.5));
        var shrink = new System.Windows.Media.Animation.DoubleAnimation(0.92, TimeSpan.FromMilliseconds(150));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => World.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    /// A new node grows in from slightly smaller.
    static void Appear(FrameworkElement card)
    {
        var scale = new ScaleTransform(0.94, 0.94);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new(0.5, 0.5);
        var to = card.Opacity;
        card.Opacity = 0;
        Fade(card, to, 180);
        var grow = new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.3, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    void DrawBackground()
    {
        var mark = new SolidColorBrush(Skin.BrushColor("Sub")) { Opacity = 0.4 };
        Dots.Drawing = board.Background switch
        {
            "grid" => new GeometryDrawing(null, new Pen(new SolidColorBrush(Skin.BrushColor("Sub")) { Opacity = 0.12 }, 1),
                Geometry.Parse("M0,0 H24 M0,0 V24")),
            "plain" => new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))),
            _ => new GeometryDrawing(mark, null, new EllipseGeometry(new Point(12, 12), 1.1, 1.1)),
        };
    }

    /// A pointer position on the board, as it's drawn right now (mid-ease too).
    Point ToBoard(Point screen) => new((screen.X - shownX) / shownZoom, (screen.Y - shownY) / shownZoom);

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
        ApplyView(ease: true, anchor: screen);
        Changed();
    }

    void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Kanban) return; // the columns scroll
        // 10% a notch; touchpads send small steps, which zoom a little each.
        ZoomAt(e.GetPosition(Surface), board.Zoom * Math.Pow(1.1, e.Delta / 120.0));
        e.Handled = true;
    }

    void ZoomIn(object sender, RoutedEventArgs e) => ZoomAt(ScreenMiddle, board.Zoom * 1.25);
    void ZoomOut(object sender, RoutedEventArgs e) => ZoomAt(ScreenMiddle, board.Zoom / 1.25);
    void ZoomReset(object sender, RoutedEventArgs e) => ZoomAt(ScreenMiddle, 1);

    /// A node's box in board units.
    Rect Bounds(BoardItem it) => cards.TryGetValue(it, out var c) && c.ActualWidth > 0
        ? new(it.X, it.Y, c.ActualWidth, c.ActualHeight) : new(it.X, it.Y, it.W + Pad, it.H > 0 ? it.H : 80);

    void Fit(object sender, RoutedEventArgs e) => Fit();

    /// Zooms and centers so every node is in view (not past 150%).
    void Fit()
    {
        if (board.Items.Count == 0) { (board.X, board.Y, board.Zoom) = (40, 40, 1); ApplyView(ease: true); Changed(); return; }
        var all = board.Items.Select(Bounds).Aggregate(Rect.Union);
        const double pad = 60;
        board.Zoom = Math.Clamp(Math.Min((Surface.ActualWidth - 2 * pad) / all.Width, (Surface.ActualHeight - 2 * pad) / all.Height), MinZoom, 1.5);
        board.X = Surface.ActualWidth / 2 - (all.X + all.Width / 2) * board.Zoom;
        board.Y = Surface.ActualHeight / 2 - (all.Y + all.Height / 2) * board.Zoom;
        ApplyView(ease: true);
        Changed();
    }

    // ---- minimap ----

    void DrawMap()
    {
        double w = Map.ActualWidth, h = Map.ActualHeight;
        if (w <= 0 || h <= 0 || Surface.ActualWidth <= 0 || Kanban) return;
        var view = new Rect(ToBoard(new Point(0, 0)), ToBoard(new Point(Surface.ActualWidth, Surface.ActualHeight)));
        if (!mapDragging)
        {
            var all = board.Items.Select(Bounds).Aggregate(view, Rect.Union);
            mapScale = Math.Min(w / all.Width, h / all.Height);
            mapOffset = new Vector((w - all.Width * mapScale) / 2 - all.X * mapScale, (h - all.Height * mapScale) / 2 - all.Y * mapScale);
        }
        Map.Children.Clear();
        var node = Skin.Brush("Sub");
        foreach (var it in board.Items) Map.Children.Add(MapRect(Bounds(it), it.Kind == "group" ? null : node, it.Kind == "group" ? node : null, 0.55));
        Map.Children.Add(MapRect(view, null, Skin.Brush("Accent"), 1));
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

    // ---- pointer on the background: pan, menus, add ----

    // Left or middle drag on the background pans; a left double-click adds a note, a right click opens the board menu there.
    // Nodes and their controls handle their own clicks, so only the background (and middle drags anywhere) get here.
    void SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        Focus(); // for Ctrl+V; also takes focus out of a node being edited
        if (Kanban) return;
        var p = e.GetPosition(Surface);
        if (NodeAt(ToBoard(p)) is null) Select(null); // empty board (a locked node lets clicks through, but stays picked)
        if (e.ChangedButton == MouseButton.Right) { BoardMenu(ToBoard(p), p).IsOpen = true; return; }
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
            var at = ToBoard(p);
            r.Item.W = Math.Clamp(r.W + at.X - r.From.X, MinW, MaxW);
            if (r.Item.Kind == "group")
            {
                r.Item.H = Math.Clamp(r.H + at.Y - r.From.Y, 80, 4000);
                (r.Card.Width, r.Card.Height) = (r.Item.W + Pad, r.Item.H);
            }
            else r.Inner.Width = r.Item.W;
            Redraw();
        }
        else if (dragging is { } d)
        {
            var at = ToBoard(p) - d.Grab;
            (d.Item.X, d.Item.Y) = (at.X, at.Y);
            Place(d.Item, d.Card);
            foreach (var (it, card, off) in d.Carry)
            {
                (it.X, it.Y) = (at.X + off.X, at.Y + off.Y);
                Place(it, card);
            }
            Redraw();
        }
        else if (connecting is { } c)
        {
            var at = ToBoard(p);
            (c.Rubber.X2, c.Rubber.Y2) = (at.X, at.Y);
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
        if (connecting is { } c)
        {
            World.Children.Remove(c.Rubber);
            connecting = null;
            if (NodeAt(ToBoard(e.GetPosition(Surface)), c.From) is { } to) Connect(c.From, to);
        }
        else if (panFrom is null && dragging is null && resizing is null) return;
        panFrom = null;
        dragging = null;
        resizing = null;
        Surface.ReleaseMouseCapture();
        Surface.Cursor = null;
        if (penBeforePan is { } pen) SetPen(pen); // back to drawing after a Ctrl-drag
        penBeforePan = null;
        Changed();
    }

    /// The pen or eraser that was on when a Ctrl-drag started (null: neither).
    InkCanvasEditingMode? penBeforePan;

    // Ctrl + left drag pans from anywhere: over nodes, and with the pen or eraser on (which pause for the drag, so nothing is drawn).
    void SurfacePreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (Kanban || e.ChangedButton != MouseButton.Left || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true; // nodes, ink and the rest never see this press
        Focus();
        if (Ink.EditingMode != InkCanvasEditingMode.None)
        {
            penBeforePan = Ink.EditingMode;
            Ink.EditingMode = InkCanvasEditingMode.None;
        }
        panFrom = e.GetPosition(Surface);
        Surface.CaptureMouse();
        Surface.Cursor = Cursors.SizeAll;
    }

    /// The topmost node under a board point (other than `not`), preferring nodes over the groups they sit in.
    BoardItem? NodeAt(Point at, BoardItem? not = null) =>
        board.Items.AsEnumerable().Reverse().Where(i => i != not && Bounds(i).Contains(at)).OrderBy(i => i.Kind == "group" ? 1 : 0).FirstOrDefault();

    void AddClick(object sender, RoutedEventArgs e) => Open(AddMenu(Kanban ? null : Middle), AddButton);

    void MoreClick(object sender, RoutedEventArgs e) => Open(BoardMenu(Kanban ? new Point() : Middle, null), MoreButton);

    static void Open(ContextMenu menu, UIElement under)
    {
        menu.PlacementTarget = under;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- nodes: the card around each one (drag, resize, connect, menu) ----

    void Add(BoardItem it, Point at, bool focus = false)
    {
        (it.X, it.Y) = (at.X, at.Y);
        if (Kanban && it.Column == "") it.Column = board.Columns.FirstOrDefault() ?? "";
        board.Items.Add(it);
        if (Kanban) DrawKanban(focus ? it : null);
        else
        {
            Show(it, focus);
            Appear(cards[it]);
        }
        Changed();
    }

    void NewNote(Point at, string text) => Add(new() { Text = text }, at, focus: text == "");

    void NewPicture(Point at, BitmapSource? image = null, string? copyFrom = null)
    {
        var it = new BoardItem { Kind = "image" };
        Directory.CreateDirectory(BoardStore.Files);
        it.File = it.Id + (copyFrom is null ? ".png" : IOPath.GetExtension(copyFrom).ToLowerInvariant());
        var path = IOPath.Combine(BoardStore.Files, it.File);
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

    /// A node's card on the canvas.
    void Show(BoardItem it, bool focus = false)
    {
        var (body, edit, cleanup) = Build(it, focus);
        if (cleanup is not null) cleanups[it] = cleanup;
        var group = it.Kind == "group";

        var remove = new Button
        {
            Style = Skin.Style("Ghost"), Content = "", FontFamily = Skin.Icons, FontSize = 10,
            Padding = new(5), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Remove", Opacity = 0,
        };
        var lockMark = new TextBlock
        {
            Text = "", FontFamily = Skin.Icons, FontSize = 10, Foreground = Skin.Brush("Sub"),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Locked (right-click to unlock)",
            Visibility = it.Locked ? Visibility.Visible : Visibility.Collapsed,
        };
        var head = new Grid { Height = 22, Children = { lockMark, remove } };
        DockPanel.SetDock(head, Dock.Top);
        var inner = new DockPanel { Children = { head, body } };
        if (!group) inner.Width = it.W;
        // Resize: drag the bottom-right corner (width; a group's height too).
        var grip = new Border
        {
            Width = 16, Height = 16, Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, -10, -10),
            ToolTip = "Drag to resize",
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M10,2 L2,10 M10,6 L6,10"), Stroke = Skin.Brush("Sub"), StrokeThickness = 1.3 },
        };
        // Connect: drag from the dot on the right edge onto another node.
        var handle = new Ellipse
        {
            Width = 12, Height = 12, Fill = Skin.Brush("Popup"), Stroke = Skin.Brush("Accent"), StrokeThickness = 2, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, -19, 0),
            Cursor = Cursors.Cross, ToolTip = "Drag onto another node to connect",
        };
        var card = new Border
        {
            Background = group ? GroupFill(it) : Skin.Brush("Popup"), BorderBrush = Edge(it), BorderThickness = new(1),
            CornerRadius = new(group ? 16 : 12), Padding = new(12, 2, 12, 12), Cursor = Cursors.SizeAll,
            Child = new Grid { Children = { inner, grip, handle } },
        };
        if (group) (card.Width, card.Height) = (it.W + Pad, it.H > 0 ? it.H : it.H = 240);
        Panel.SetZIndex(card, group ? -2 : 0);
        void Chrome(double to) { Fade(remove, to); Fade(handle, to); Fade(grip, to); }
        card.MouseEnter += (_, _) => Chrome(it.Locked ? 0 : 1);
        card.MouseLeave += (_, _) => Chrome(0);
        card.SizeChanged += (_, _) => Redraw();
        remove.Click += (_, _) => Remove(it);
        grip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (it.Locked) return;
            resizing = (it, card, inner, ToBoard(e.GetPosition(Surface)), it.W, it.H);
            Surface.CaptureMouse();
        };
        handle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var from = Bounds(it);
            var rubber = new Line
            {
                X1 = from.X + from.Width / 2, Y1 = from.Y + from.Height / 2, X2 = from.Right, Y2 = from.Y + from.Height / 2,
                Stroke = Skin.Brush("Accent"), StrokeThickness = 2, StrokeDashArray = [4, 3], IsHitTestVisible = false,
            };
            Panel.SetZIndex(rubber, 5);
            World.Children.Add(rubber);
            connecting = (it, rubber);
            Surface.CaptureMouse();
        };
        // Drag by anything that doesn't keep its own clicks (text boxes, buttons, task boxes, links); double-click edits.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && edit is not null) { edit(true); e.Handled = true; return; }
            if (it.Locked) return; // the background takes it: a locked node pans the board instead
            e.Handled = true;
            if (!group) ToFront(it);
            var at = ToBoard(e.GetPosition(Surface));
            // A group carries what's inside it.
            var box = Bounds(it);
            var carry = group
                ? board.Items.Where(o => o != it && !o.Locked && box.Contains(Bounds(o))).Select(o => (o, cards[o], new Point(o.X, o.Y) - new Point(it.X, it.Y))).ToList()
                : [];
            dragging = (it, card, at - new Point(it.X, it.Y), carry);
            Surface.CaptureMouse();
        };
        NodeMenuOn(card, it, edit);
        cards[it] = card;
        Place(it, card);
        World.Children.Add(card);
    }

    /// The card's edge: its color when it has one.
    Brush Edge(BoardItem it) => ColorOf(it.Color) is { } c ? new SolidColorBrush(c) : Skin.Brush(it.Kind == "group" ? "Line" : "CardEdge");

    Brush GroupFill(BoardItem it) => ColorOf(it.Color) is { } c ? new SolidColorBrush(Color.FromArgb(0x16, c.R, c.G, c.B)) : Skin.Brush("CardBg");

    /// JSON Canvas's preset colors "1".."6".
    public static readonly (string Id, string Name, Color Color)[] NodeColors =
    [
        ("1", "Red", Color.FromRgb(0xFF, 0x5A, 0x5A)), ("2", "Orange", Color.FromRgb(0xFF, 0x8A, 0x3D)), ("3", "Yellow", Color.FromRgb(0xF5, 0xC5, 0x3D)),
        ("4", "Green", Color.FromRgb(0x4C, 0xC3, 0x7A)), ("5", "Cyan", Color.FromRgb(0x2C, 0xC7, 0xC7)), ("6", "Purple", Color.FromRgb(0xB0, 0x7C, 0xFF)),
    ];

    static Color? ColorOf(string id) => NodeColors.FirstOrDefault(c => c.Id == id) is { Name: not null } c ? c.Color : null;

    void ToFront(BoardItem it)
    {
        board.Items.Remove(it); // the end of the list is the top of the stack
        board.Items.Add(it);
        if (cards.TryGetValue(it, out var card) && !Kanban) { World.Children.Remove(card); World.Children.Add(card); }
    }

    void ToBack(BoardItem it)
    {
        board.Items.Remove(it);
        board.Items.Insert(0, it);
        if (cards.TryGetValue(it, out var card) && !Kanban) { World.Children.Remove(card); World.Children.Insert(0, card); }
        Changed();
    }

    /// Builds a node again in place (after its settings changed).
    void Rebuild(BoardItem it)
    {
        if (Kanban) { DrawKanban(); return; }
        if (!cards.ContainsKey(it)) return;
        var at = World.Children.IndexOf(cards[it]);
        Detach(it);
        Show(it);
        // Back to its place in the stack.
        var card = cards[it];
        World.Children.Remove(card);
        World.Children.Insert(Math.Min(at, World.Children.Count), card);
        ApplyTag();
        Changed();
    }

    /// Takes a node's card off the board and stops what it was doing (the item itself stays).
    void Detach(BoardItem it)
    {
        if (cleanups.Remove(it, out var stop)) stop();
        edits.Remove(it);
        if (cards.Remove(it, out var card)) World.Children.Remove(card);
    }

    void Remove(BoardItem it)
    {
        if (!board.Items.Contains(it)) return; // already gone (an emptied node removes itself as focus leaves)
        if (it == selected) Select(null);
        board.Items.Remove(it); // Ctrl+Z brings it back (its picture file stays until the next start)
        board.Edges.RemoveAll(e => e.From == it.Id || e.To == it.Id);
        var leaving = Kanban ? null : cards.GetValueOrDefault(it);
        Detach(it);
        if (leaving is not null) Vanish(leaving);
        if (Kanban) DrawKanban();
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
            b.UriSource = new Uri(IOPath.Combine(BoardStore.Files, it.File!));
            b.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    // ---- paste and drop ----

    /// Whatever's on the clipboard, as nodes at a board point. False when there's nothing usable.
    bool Paste(Point at)
    {
        if (Clipboard.ContainsFileDropList()) AddFiles(Clipboard.GetFileDropList().Cast<string>(), at);
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) NewPicture(at, img);
        else if (Clipboard.ContainsText()) AddText(Clipboard.GetText(), at);
        else return false;
        return true;
    }

    void Dropped(object sender, DragEventArgs e)
    {
        if (Kanban) return;
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

    /// Pictures become pictures; other files and folders become links. Obsidian .canvas files are imported as a new board.
    void AddFiles(IEnumerable<string> files, Point at)
    {
        foreach (var f in files)
        {
            var ext = IOPath.GetExtension(f).ToLowerInvariant();
            if (ext == ".canvas") { ImportCanvas(f); continue; }
            if (ext == BoardFile.Extension) { ImportBoardFile(f); continue; }
            if (Pictures.Contains(ext)) NewPicture(at, copyFrom: f);
            else Add(new() { Kind = "link", W = 260, Text = f }, at);
            at.Offset(24, 24);
        }
    }
}
