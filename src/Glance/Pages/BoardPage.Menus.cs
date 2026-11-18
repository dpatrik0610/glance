using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Glance.Services;
using IOPath = System.IO.Path;

namespace Glance.Pages;

/// Right-click menus (node, board, tab), the Add menu, and what they do: tidy, export, import.
public partial class BoardPage
{
    static MenuItem Item(ItemsControl into, string header, Action act, bool enabled = true, bool check = false)
    {
        var i = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = check };
        i.Click += (_, e) => { e.Handled = true; act(); };
        into.Items.Add(i);
        return i;
    }

    static MenuItem Sub(ItemsControl into, string header)
    {
        var i = new MenuItem { Header = header };
        into.Items.Add(i);
        return i;
    }

    /// Right-click on a node opens its menu (and keeps the board's own menu shut). Text boxes keep their own.
    void NodeMenuOn(FrameworkElement card, BoardItem it, Action<bool>? edit)
    {
        card.MouseRightButtonDown += (_, e) => e.Handled = true;
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (e.OriginalSource is DependencyObject d && InTextBox(d)) return;
            var menu = NodeMenu(it, edit);
            menu.PlacementTarget = card;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
    }

    static bool InTextBox(DependencyObject d)
    {
        for (DependencyObject? x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is TextBox) return true;
        return false;
    }

    ContextMenu NodeMenu(BoardItem it, Action<bool>? edit)
    {
        var m = new ContextMenu();
        if (edit is not null) Item(m, it.Kind == "embed" ? "Open in Notes" : "Edit", () => edit(true));
        if (it.Kind == "link") Item(m, "Open", () => Open(it.Text.Trim().Trim('"')));
        if (it.Kind == "image") Item(m, "Open", () => Open(IOPath.Combine(BoardStore.Files, it.File ?? "")));
        Item(m, "Duplicate", () => Duplicate(it));
        var copy = it.Kind switch { "link" => "Copy address", "image" => "Copy picture", "note" => "Copy text", _ => null };
        if (copy is not null) Item(m, copy, () => Copy(it));
        if (it.Kind == "image") Item(m, "Save picture as…", () => SavePicture(it));
        if (it.Kind == "note") Item(m, "Pin to desktop as a sticky", () => PinSticky(it));
        m.Items.Add(new Separator());

        var color = Sub(m, "Color");
        Item(color, "None", () => SetColor(it, ""), check: it.Color == "").Icon =
            new Ellipse { Width = 10, Height = 10, Stroke = (Brush)FindResource("Sub"), StrokeThickness = 1.2 };
        foreach (var (id, name, c) in NodeColors)
        {
            var i = Item(color, name, () => SetColor(it, id), check: it.Color == id);
            i.Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(c) };
        }
        if (!Kanban)
        {
            Item(m, "Bring to front", () => { ToFront(it); Changed(); });
            Item(m, "Send to back", () => ToBack(it));
            Item(m, it.Locked ? "Unlock" : "Lock in place", () => { it.Locked = !it.Locked; Rebuild(it); });
            if (board.Edges.Any(e => e.From == it.Id || e.To == it.Id))
                Item(m, "Remove its arrows", () => { board.Edges.RemoveAll(e => e.From == it.Id || e.To == it.Id); Changed(); });
        }
        var others = boards.All.Where(b => b != board).ToList();
        var move = Sub(m, "Move to board");
        move.IsEnabled = others.Count > 0;
        foreach (var b in others) Item(move, b.Name, () => MoveTo(it, b));
        m.Items.Add(new Separator());
        Item(m, "Delete", () => Remove(it));
        return m;
    }

    void SetColor(BoardItem it, string id)
    {
        it.Color = id;
        Rebuild(it);
    }

    void Duplicate(BoardItem it)
    {
        var copy = new BoardItem
        {
            Kind = it.Kind, X = it.X + 24, Y = it.Y + 24, W = it.W, H = it.H, Title = it.Title, Text = it.Text, Color = it.Color, Column = it.Column,
        };
        if (it.File is not null)
        {
            copy.File = copy.Id + IOPath.GetExtension(it.File);
            try { File.Copy(IOPath.Combine(BoardStore.Files, it.File), IOPath.Combine(BoardStore.Files, copy.File)); }
            catch (IOException) { return; }
        }
        board.Items.Add(copy);
        if (Kanban) DrawKanban();
        else
        {
            Show(copy);
            Appear(cards[copy]);
        }
        Changed();
    }

    void Copy(BoardItem it)
    {
        try
        {
            if (it.Kind == "image" && LoadPicture(it) is { } pic) Clipboard.SetImage(pic);
            else ClipboardHistory.Copy(it.Kind == "note" && it.Title.Trim() != "" ? $"# {it.Title}\n\n{it.Text}" : it.Text);
        }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy
    }

    void SavePicture(BoardItem it)
    {
        var src = IOPath.Combine(BoardStore.Files, it.File ?? "");
        var ext = IOPath.GetExtension(src);
        var pick = new Microsoft.Win32.SaveFileDialog { FileName = (it.Title.Trim() is { Length: > 0 } t ? t : "Picture") + ext, Filter = $"Picture|*{ext}" };
        if (pick.ShowDialog() != true) return;
        try { File.Copy(src, pick.FileName, true); }
        catch (IOException e) { Dialog.Info("Board", "Couldn't save the picture", e.Message); }
    }

    static void PinSticky(BoardItem it)
    {
        var s = new Sticky { Kind = NoteKind.Text, Title = it.Title, Text = it.Text };
        Stickies.Items.Insert(0, s);
        Stickies.Reveal(s);
    }

    void MoveTo(BoardItem it, Board to)
    {
        board.Items.Remove(it);
        board.Edges.RemoveAll(e => e.From == it.Id || e.To == it.Id);
        Detach(it);
        if (to.Layout == "kanban") it.Column = "";
        to.Items.Add(it);
        if (Kanban) DrawKanban();
        Changed();
    }

    /// The Add menu: at a board point (canvas), or into the first column (Kanban, at = null).
    ContextMenu AddMenu(Point? at)
    {
        var m = new ContextMenu();
        Fill(m, at ?? new Point());
        return m;
    }

    void Fill(ItemsControl m, Point at)
    {
        Item(m, "Note", () => NewNote(at, ""));
        Item(m, "Link (website, file or folder)", () => Add(new() { Kind = "link", W = 260 }, at, focus: true));
        if (!Kanban) Item(m, "Group", () => Add(new() { Kind = "group", W = 400, H = 280 }, at, focus: true));
        Item(m, "Live stat (CPU, GPU, RAM, network)", () => Add(new() { Kind = "stat", Text = Metrics[0] }, at));
        var embed = Sub(m, "A note from Notes");
        embed.IsEnabled = NoteStore.Notes.Count > 0;
        foreach (var n in NoteStore.Notes.Take(40))
            Item(embed, n.DisplayTitle, () => Add(new() { Kind = "embed", Text = n.Id, W = 260 }, at));
        Item(m, "Picture from a file…", () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|" + string.Join(";", Pictures.Select(x => "*" + x)), Multiselect = true };
            if (pick.ShowDialog() == true) AddFiles(pick.FileNames, at);
        });
    }

    /// Right-click on the board's background (screen: where, for "zoom here"), or the ⋯ button.
    ContextMenu BoardMenu(Point at, Point? screen)
    {
        var m = new ContextMenu();
        if (!Kanban)
        {
            Fill(Sub(m, "Add"), at);
            Item(m, "Paste here", () => Paste(at), Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList());
            m.Items.Add(new Separator());
            Item(m, "Zoom to 100% here", () => ZoomAt(screen ?? ScreenMiddle, 1));
            Item(m, "Fit everything", Fit);
            Item(m, "Tidy up (snap to the grid)", Tidy, board.Items.Count > 0);
            var bg = Sub(m, "Background");
            foreach (var (id, name) in new[] { ("dots", "Dots"), ("grid", "Grid"), ("plain", "Plain") })
                Item(bg, name, () => { board.Background = id; DrawBackground(); Changed(); }, check: board.Background == id);
        }
        var layout = Sub(m, "Layout");
        Item(layout, "Canvas", () => SetLayout("canvas"), check: !Kanban);
        Item(layout, "Kanban (columns)", () => SetLayout("kanban"), check: Kanban);
        if (!Kanban)
        {
            Item(m, "Clear the drawing", () =>
            {
                if (Dialog.Confirm("Clear the drawing", "Erase everything drawn with the pen?", "Notes and other nodes stay.", "Clear"))
                {
                    Ink.Strokes.Clear();
                    SaveInk();
                }
            }, Ink.Strokes.Count > 0);
            m.Items.Add(new Separator());
            Item(m, "Export as picture…", ExportPicture, board.Items.Count > 0 || Ink.Strokes.Count > 0);
        }
        Item(m, "Export as Obsidian canvas…", ExportCanvas);
        Item(m, "Import Obsidian canvas…", () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Obsidian canvas|*.canvas" };
            if (pick.ShowDialog() == true) ImportCanvas(pick.FileName);
        });
        return m;
    }

    ContextMenu TabMenu(Board b, RadioButton chip)
    {
        var m = new ContextMenu();
        var at = boards.All.IndexOf(b);
        Item(m, "Rename", () => Rename(b, chip));
        Item(m, "Move left", () => MoveBoard(b, -1), at > 0);
        Item(m, "Move right", () => MoveBoard(b, 1), at < boards.All.Count - 1);
        Item(m, "Delete", () => DeleteBoard(b));
        return m;
    }

    void SetLayout(string layout)
    {
        board.Layout = layout;
        Refresh();
    }

    /// Snaps every node's corner and width to the dot grid.
    void Tidy()
    {
        static double Snap(double v) => Math.Round(v / Tile) * Tile;
        foreach (var it in board.Items.Where(i => !i.Locked))
        {
            (it.X, it.Y) = (Snap(it.X), Snap(it.Y));
            it.W = Math.Max(MinW, Snap(it.W + Pad) - Pad);
            if (it.H > 0) it.H = Snap(it.H);
        }
        Refresh();
    }

    // ---- export / import ----

    /// The whole board (nodes, arrows, drawing) as a PNG, on the glass color, up to 8000 px a side.
    void ExportPicture()
    {
        var all = board.Items.Select(Bounds).Aggregate(Rect.Empty, Rect.Union);
        if (Ink.Strokes.Count > 0)
        {
            var ink = Ink.Strokes.GetBounds();
            ink.Offset(-50000, -50000);
            all.Union(ink);
        }
        if (all.IsEmpty) return;
        all.Inflate(40, 40);
        var scale = Math.Min(2, 8000 / Math.Max(all.Width, all.Height));
        var size = new Size(Math.Ceiling(all.Width * scale), Math.Ceiling(all.Height * scale));
        var pick = new Microsoft.Win32.SaveFileDialog { FileName = board.Name + ".png", Filter = "PNG picture|*.png" };
        if (pick.ShowDialog() != true) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Skin.Get("PanelColor")), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(World) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = all, Stretch = Stretch.Fill }, null, new Rect(size));
        }
        var bmp = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        try
        {
            using var f = File.Create(pick.FileName);
            png.Save(f);
        }
        catch (IOException e) { Dialog.Info("Board", "Couldn't save the picture", e.Message); }
    }

    void ExportCanvas()
    {
        var pick = new Microsoft.Win32.SaveFileDialog { FileName = board.Name + ".canvas", Filter = "Obsidian canvas|*.canvas" };
        if (pick.ShowDialog() != true) return;
        try { JsonCanvas.Export(board, Kanban ? null : Bounds, pick.FileName); }
        catch (IOException e) { Dialog.Info("Board", "Couldn't export the board", e.Message); }
    }

    /// A .canvas file becomes a new board tab.
    void ImportCanvas(string file)
    {
        Board b;
        try { b = JsonCanvas.Import(file); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Dialog.Info("Board", "Couldn't read that canvas", e.Message);
            return;
        }
        boards.All.Add(b);
        Switch(b);
        DrawTabs();
        Dispatcher.BeginInvoke(() => Fit(), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
