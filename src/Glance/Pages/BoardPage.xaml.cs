using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// An endless dotted canvas of notes and pictures. Board units are pixels at 100%; the view is pan (screen px) + zoom.
// ponytail: prototype. No resize, multi-select, links between items or undo yet.
public partial class BoardPage : UserControl
{
    const double Tile = 24;
    static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    readonly Board board = BoardStore.Load();
    readonly DispatcherTimer saveSoon = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<BoardItem, FrameworkElement> cards = [];
    Point? panFrom;
    (BoardItem Item, FrameworkElement Card, Vector Grab)? dragging;

    public BoardPage()
    {
        InitializeComponent();
        saveSoon.Tick += (_, _) => Save();
        Application.Current.Exit += (_, _) => { if (saveSoon.IsEnabled) Save(); };
        // The card's corners are round; keep the dots inside them.
        Surface.SizeChanged += (_, _) => Surface.Clip = new RectangleGeometry(new Rect(Surface.RenderSize), 17, 17);
        foreach (var it in board.Items) Show(it);
        ApplyView();
    }

    void Save()
    {
        saveSoon.Stop();
        BoardStore.Save(board);
    }

    void Changed()
    {
        saveSoon.Stop();
        saveSoon.Start();
        Hint.Visibility = board.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- view ----

    void ApplyView()
    {
        World.RenderTransform = new MatrixTransform(board.Zoom, 0, 0, board.Zoom, board.X, board.Y);
        Dots.Viewport = new Rect(board.X, board.Y, Tile * board.Zoom, Tile * board.Zoom);
        ZoomText.Text = $"{board.Zoom:P0}";
        Hint.Visibility = board.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Point ToBoard(Point screen) => new((screen.X - board.X) / board.Zoom, (screen.Y - board.Y) / board.Zoom);

    /// Where new things go when there's no pointer position: the middle of the view.
    Point Middle => ToBoard(new Point(Surface.ActualWidth / 2 - 110, Surface.ActualHeight / 2 - 40));

    void Wheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(Surface);
        var zoom = Math.Clamp(board.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.25, 3);
        var f = zoom / board.Zoom;
        // Keep the point under the cursor where it is.
        board.X = p.X - (p.X - board.X) * f;
        board.Y = p.Y - (p.Y - board.Y) * f;
        board.Zoom = zoom;
        ApplyView();
        Changed();
        e.Handled = true;
    }

    void ResetView(object sender, RoutedEventArgs e)
    {
        (board.X, board.Y, board.Zoom) = (40, 40, 1);
        ApplyView();
        Changed();
    }

    // Left or middle drag on the background pans; a left double-click adds a note there. Cards and their text boxes
    // handle their own clicks, so only the background (and middle drags anywhere) get here.
    void SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        Focus(); // for Ctrl+V; also takes focus out of a note being edited
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { NewNote(ToBoard(e.GetPosition(Surface)), ""); return; }
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        panFrom = e.GetPosition(Surface);
        Surface.CaptureMouse();
        Surface.Cursor = Cursors.SizeAll;
    }

    void SurfaceMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        if (dragging is { } d)
        {
            var at = ToBoard(p) - d.Grab;
            (d.Item.X, d.Item.Y) = (at.X, at.Y);
            Place(d.Item, d.Card);
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
        if (panFrom is null && dragging is null) return;
        panFrom = null;
        dragging = null;
        Surface.ReleaseMouseCapture();
        Surface.Cursor = null;
        Changed();
    }

    // ---- items ----

    void AddNote(object sender, RoutedEventArgs e) => NewNote(Middle, "");

    void NewNote(Point at, string text)
    {
        var it = new BoardItem { X = at.X, Y = at.Y, Text = text };
        board.Items.Add(it);
        Show(it, focus: text == "");
        Changed();
    }

    void NewPicture(Point at, BitmapSource? image = null, string? copyFrom = null)
    {
        var it = new BoardItem { Kind = "image", X = at.X, Y = at.Y, W = 320 };
        Directory.CreateDirectory(BoardStore.Files);
        it.File = it.Id + (copyFrom is null ? ".png" : Path.GetExtension(copyFrom).ToLowerInvariant());
        var path = Path.Combine(BoardStore.Files, it.File);
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
        board.Items.Add(it);
        Show(it);
        Changed();
    }

    void Show(BoardItem it, bool focus = false)
    {
        FrameworkElement body;
        Action<bool>? edit = null;
        if (it.Kind == "image")
            body = new Image { Source = LoadPicture(it), MaxWidth = it.W, Stretch = Stretch.Uniform };
        else
        {
            // A note shows its Markdown drawn; double-click switches to editing the title and source, leaving the card switches back.
            TextBox Box(string text, string hint, double size, FontWeight weight) => new()
            {
                Text = text, Tag = hint, AcceptsReturn = size < 15, TextWrapping = TextWrapping.Wrap, Width = it.W, Cursor = Cursors.IBeam,
                Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), FontSize = size, FontWeight = weight,
            };
            var title = Box(it.Title, "Title (optional)", 15, FontWeights.SemiBold);
            var box = Box(it.Text, "Markdown: **bold**, - [ ] task, # heading…", 14, FontWeights.Normal);
            title.Margin = new(0, 0, 0, 6);
            var editor = new StackPanel { Children = { title, box }, Visibility = Visibility.Collapsed };
            var view = new StackPanel { Width = it.W };
            title.TextChanged += (_, _) => { it.Title = title.Text; Changed(); };
            box.TextChanged += (_, _) => { it.Text = box.Text; Changed(); };
            void Draw()
            {
                view.Children.Clear();
                if (it.Title.Trim() != "")
                    view.Children.Add(new TextBlock { Text = it.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
                view.Children.Add(it.Text.Trim() == "" && it.Title.Trim() == ""
                    ? new TextBlock { Text = "Empty note", Style = (Style)FindResource("Label") }
                    : MarkdownView.Render(it.Text, at => box.Text = MarkdownView.Toggle(it.Text, at))); // box saves it
            }
            edit = on =>
            {
                editor.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                view.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                if (on) FocusWhenVisible(box);
                else Draw();
            };
            body = new Grid { Children = { view, editor } };
            body.IsKeyboardFocusWithinChanged += (_, e) =>
            {
                if (e.NewValue is true || !editor.IsVisible) return;
                edit(false);
                if (it.Title.Trim() == "" && it.Text.Trim() == "") Dispatcher.BeginInvoke(() => Remove(it)); // left empty
            };
            edit(focus);
        }

        var remove = new Button
        {
            Style = (Style)FindResource("Ghost"), Content = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10,
            Padding = new(5), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Remove", Opacity = 0,
        };
        var head = new Grid { Height = 22, Children = { remove } };
        DockPanel.SetDock(head, Dock.Top);
        var card = new Border
        {
            Background = (Brush)FindResource("Popup"), BorderBrush = (Brush)FindResource("CardEdge"), BorderThickness = new(1),
            CornerRadius = new(12), Padding = new(12, 2, 12, 12), Cursor = Cursors.SizeAll,
            Child = new DockPanel { Children = { head, body } },
        };
        card.MouseEnter += (_, _) => remove.Opacity = 1;
        card.MouseLeave += (_, _) => remove.Opacity = 0;
        remove.Click += (_, _) => Remove(it);
        // Drag by anything but the text boxes, task boxes and links (which keep their clicks); double-click edits a note.
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
            b.UriSource = new Uri(Path.Combine(BoardStore.Files, it.File!));
            b.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    void Remove(BoardItem it)
    {
        if (!board.Items.Contains(it)) return; // already gone (an emptied note removes itself as focus leaves)
        if (it.Kind == "note" && (it.Title + it.Text).Trim().Length > 0
            && !Dialog.Confirm("Remove note", "Remove this note from the board?", "This can't be undone.", "Remove")) return;
        board.Items.Remove(it);
        World.Children.Remove(cards[it]);
        cards.Remove(it);
        if (it.File is not null) try { File.Delete(Path.Combine(BoardStore.Files, it.File)); } catch (IOException) { }
        Changed();
    }

    // ---- paste and drop ----

    void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || e.OriginalSource is TextBox) return;
        var at = Surface.IsMouseOver ? ToBoard(Mouse.GetPosition(Surface)) : Middle;
        if (Clipboard.ContainsFileDropList()) AddFiles(Clipboard.GetFileDropList().Cast<string>(), at);
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) NewPicture(at, img);
        else if (Clipboard.ContainsText()) NewNote(at, Clipboard.GetText());
        else return;
        e.Handled = true;
    }

    void Dropped(object sender, DragEventArgs e)
    {
        var at = ToBoard(e.GetPosition(Surface));
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files, at);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text) NewNote(at, text);
    }

    /// Pictures become pictures; any other file becomes a note with its path.
    void AddFiles(IEnumerable<string> files, Point at)
    {
        foreach (var f in files)
        {
            if (Pictures.Contains(Path.GetExtension(f).ToLowerInvariant())) NewPicture(at, copyFrom: f);
            else NewNote(at, f);
            at.Offset(24, 24);
        }
    }
}
