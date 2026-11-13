using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance;

/// A folder as nested rectangles, each item's area its share of the bytes. One colour per item of the folder shown;
/// everything inside it is a shade of that colour. Each of those items is drawn once into its own Drawing, so the motion
/// is cheap: blocks pop in biggest first, going into a folder zooms out of its block (and back up into it), hovering one
/// dims the rest, and a recycled item shrinks away in red. Frames are drawn only while something moves.
public sealed class Treemap : FrameworkElement
{
    static readonly Color[] Hues =
    [
        Color.FromRgb(0xFF, 0x6A, 0x3D), Color.FromRgb(0x3D, 0xA9, 0xFF), Color.FromRgb(0x4C, 0xC3, 0x8A), Color.FromRgb(0xF2, 0xB3, 0x3D),
        Color.FromRgb(0x9B, 0x7B, 0xFF), Color.FromRgb(0x2C, 0xC7, 0xC7), Color.FromRgb(0xFF, 0x5C, 0x8A), Color.FromRgb(0x8A, 0x8F, 0x98),
    ];
    static readonly byte[] Shades = [0xE0, 0xB4, 0x90, 0x70];
    static readonly Brush[][] Fills = Hues.Select(h => Shades.Select(a => Frozen<Brush>(new SolidColorBrush(Color.FromArgb(a, h.R, h.G, h.B)))).ToArray()).ToArray();
    static readonly Pen Outline = Frozen(new Pen(Brushes.White, 1.5));
    static readonly Brush LabelBg = Frozen<Brush>(new SolidColorBrush(Color.FromArgb(0xCC, 0x16, 0x16, 0x18)));
    static readonly Brush LabelText = Frozen<Brush>(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5)));
    static readonly Brush Hole = Frozen<Brush>(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1A)));
    static readonly Brush Doom = Frozen<Brush>(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A)));
    static readonly Typeface Face = new("Segoe UI Variable Text, Segoe UI");
    const double RevealS = 0.32, ZoomS = 0.42, VanishS = 0.4;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// Top: index of the block (item of the folder shown) this cell is in.
    readonly record struct Cell(Rect Rect, Node Node, int Top);
    sealed class Block { public required Node Node; public required Drawing Drawing; public Rect Rect; public double Delay, Lit = 1; }

    readonly List<Cell> cells = [];
    readonly List<Block> blocks = [];
    readonly Dictionary<Node, Rect> placed = []; // every item drawn, folders included
    readonly Stopwatch clock = Stopwatch.StartNew();
    Node? root;
    Cell? hover;
    double revealAt = -10, zoomAt = -10, last;
    Rect zoomFrom; // where the whole map sits as a zoom starts
    (Rect Rect, double At, Action Done)? vanish;
    bool running;

    /// The item under the pointer changed (null: none).
    public event Action<Node?>? Hovered;
    /// A click: the item of the folder shown that was clicked into.
    public event Action<Node>? Entered;

    public Treemap() => IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Kick(); else Stop(); };

    /// Setting a child of the folder shown zooms into it; setting the parent zooms back out; anything else pops in.
    public Node? Root
    {
        get => root;
        set
        {
            var old = root;
            Rect? into = value is not null && old is not null && value.Parent == old && placed.TryGetValue(value, out var r) ? r : null;
            root = value;
            Rebuild();
            var now = clock.Elapsed.TotalSeconds;
            revealAt = now;
            if (into is { } from) Zoom(from, now);
            else if (old is not null && old.Parent == value && placed.TryGetValue(old, out var back)) Zoom(Around(back), now);
        }
    }

    /// The file or folder under the pointer, for the right-click menu.
    public Node? Hit => hover?.Node;

    Rect Full => new(0, 0, ActualWidth, ActualHeight);

    // Zooming in, the map starts inside the block clicked; zooming out, it starts so big that the folder left fills the view.
    void Zoom(Rect from, double now)
    {
        (zoomFrom, zoomAt) = (from, now);
        revealAt = -10; // no pop-in under a zoom
        Kick();
    }

    Rect Around(Rect r)
    {
        double sx = ActualWidth / r.Width, sy = ActualHeight / r.Height;
        return new Rect(-r.X * sx, -r.Y * sy, ActualWidth * sx, ActualHeight * sy);
    }

    /// Shows the item shrinking away in red, then calls done (which takes it out of the tree and sets Root again).
    public void Vanish(Node node, Action done)
    {
        if (!placed.TryGetValue(node, out var r)) { done(); return; }
        vanish = (r, clock.Elapsed.TotalSeconds, done);
        Kick();
    }

    public void Rebuild()
    {
        cells.Clear();
        blocks.Clear();
        placed.Clear();
        hover = null;
        if (root is not null && ActualWidth > 0 && ActualHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var kids = root.Kids!.Where(k => k.Size > 0).ToList(); // biggest first already
            var rects = Squarify(kids.Select(k => (double)k.Size).ToList(), Full);
            for (var i = 0; i < kids.Count; i++)
            {
                var (kid, r) = (kids[i], rects[i]);
                if (r.Width < 1 || r.Height < 1) continue; // too thin to see
                var b = blocks.Count;
                var start = cells.Count;
                Place(kid, r, b);
                var g = new DrawingGroup();
                using (var dc = g.Open())
                {
                    for (var j = start; j < cells.Count; j++)
                    {
                        var cr = cells[j].Rect;
                        if (cr.Width > 2 && cr.Height > 2) cr.Inflate(-0.5, -0.5); // a hairline between neighbours
                        dc.DrawRectangle(Fills[b % Hues.Length][(j - start) % Shades.Length], null, cr);
                    }
                    if (r.Width >= 70 && r.Height >= 24)
                    {
                        var ft = new FormattedText(kid.Lump ? "Files under 1 MB" : kid.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, LabelText, dpi)
                            { MaxTextWidth = r.Width - 18, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                        dc.DrawRoundedRectangle(LabelBg, null, new Rect(r.X + 4, r.Y + 4, ft.Width + 10, ft.Height + 4), 4, 4);
                        dc.DrawText(ft, new Point(r.X + 9, r.Y + 6));
                    }
                }
                g.Freeze();
                blocks.Add(new Block { Node = kid, Rect = r, Drawing = g, Delay = Math.Min(b * 0.028, 0.45) });
            }
        }
        Hovered?.Invoke(null);
        InvalidateVisual();
        Kick();
    }

    void Place(Node node, Rect area, int top)
    {
        placed[node] = area;
        if (!node.IsDir || node.Kids!.Count == 0 || area.Width <= 6 || area.Height <= 6) { cells.Add(new(area, node, top)); return; }
        var kids = node.Kids.Where(k => k.Size > 0).ToList();
        var rects = Squarify(kids.Select(k => (double)k.Size).ToList(), area);
        for (var i = 0; i < kids.Count; i++)
            if (rects[i].Width >= 1 && rects[i].Height >= 1) Place(kids[i], rects[i], top);
    }

    /// Squarified treemap (Bruls, Huizing, van Wijk): sizes, biggest first, become rectangles inside `area`, in rows along
    /// its shorter side; a row takes items while that keeps them closer to square.
    public static Rect[] Squarify(IReadOnlyList<double> sizes, Rect area)
    {
        var res = new Rect[sizes.Count];
        var total = sizes.Sum();
        if (total <= 0 || area.Width <= 0 || area.Height <= 0) return res;
        var scale = area.Width * area.Height / total;
        for (var i = 0; i < sizes.Count;)
        {
            var side = Math.Min(area.Width, area.Height);
            if (side <= 0) break;
            double row = 0, worst = double.MaxValue;
            var j = i;
            for (; j < sizes.Count; j++)
            {
                var sum = row + sizes[j] * scale;
                // The worst aspect ratio in the row if item j joins: its biggest (the first) or its smallest (j)
                var ratio = Math.Max(side * side * sizes[i] * scale / (sum * sum), sum * sum / (side * side * sizes[j] * scale));
                if (ratio > worst) break;
                (worst, row) = (ratio, sum);
            }
            var thick = row / side;
            var wide = area.Width >= area.Height; // the row is a column on the left; else a strip along the top
            double at = 0;
            for (var k = i; k < j; k++)
            {
                var len = sizes[k] * scale / thick;
                res[k] = wide ? new Rect(area.X, area.Y + at, thick, len) : new Rect(area.X + at, area.Y, len, thick);
                at += len;
            }
            area = wide ? new Rect(area.X + thick, area.Y, Math.Max(0, area.Width - thick), area.Height)
                : new Rect(area.X, area.Y + thick, area.Width, Math.Max(0, area.Height - thick));
            i = j;
        }
        return res;
    }

    static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - Math.Pow(1 - t, 3);

    static Rect Lerp(Rect a, Rect b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, Math.Max(0, a.Width + (b.Width - a.Width) * t), Math.Max(0, a.Height + (b.Height - a.Height) * t));

    void Kick()
    {
        if (running || !IsVisible) return;
        running = true;
        last = clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += Frame;
    }

    void Stop()
    {
        running = false;
        CompositionTarget.Rendering -= Frame;
    }

    void Frame(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        var dt = now - last;
        if (dt < 0.004) return;
        last = now;
        var moving = now - zoomAt < ZoomS || now - revealAt < RevealS + 0.5;
        var k = 1 - Math.Exp(-Math.Min(dt, 0.05) * 14);
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var lit = hover is null || hover.Value.Top == i ? 1 : 0.5;
            if (Math.Abs(lit - b.Lit) < 0.005) b.Lit = lit;
            else { b.Lit += (lit - b.Lit) * k; moving = true; }
        }
        if (vanish is { } v)
        {
            if (now - v.At >= VanishS) { vanish = null; v.Done(); }
            moving = true;
        }
        InvalidateVisual();
        if (!moving) Stop();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, Full); // so the whole area gets mouse events
        var now = clock.Elapsed.TotalSeconds;
        var zq = Ease((now - zoomAt) / ZoomS);
        if (zq < 1)
        {
            var z = Lerp(zoomFrom, Full, zq);
            dc.PushTransform(new MatrixTransform(z.Width / ActualWidth, 0, 0, z.Height / ActualHeight, z.X, z.Y));
            dc.PushOpacity(0.35 + 0.65 * zq);
        }
        foreach (var b in blocks)
        {
            var p = Ease((now - revealAt - b.Delay) / RevealS);
            if (p <= 0) continue;
            var pop = p < 1;
            if (pop)
            {
                var s = 0.8 + 0.2 * p;
                dc.PushTransform(new ScaleTransform(s, s, b.Rect.X + b.Rect.Width / 2, b.Rect.Y + b.Rect.Height / 2));
            }
            dc.PushOpacity(p * b.Lit);
            dc.DrawDrawing(b.Drawing);
            dc.Pop();
            if (pop) dc.Pop();
        }
        if (vanish is { } v)
        {
            var t = Ease((now - v.At) / VanishS);
            dc.DrawRectangle(Hole, null, v.Rect);
            var r = v.Rect;
            r.Inflate(-r.Width / 2 * t, -r.Height / 2 * t);
            dc.PushOpacity(1 - t * 0.6);
            dc.DrawRectangle(Doom, null, r);
            dc.Pop();
        }
        else if (hover is { } h && zq >= 1) dc.DrawRectangle(null, Outline, h.Rect);
        if (zq < 1) { dc.Pop(); dc.Pop(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (vanish is not null) return;
        var p = e.GetPosition(this);
        Cell? now = null;
        foreach (var c in cells)
            if (c.Rect.Contains(p)) { now = c; break; }
        if (now?.Node == hover?.Node) return;
        hover = now;
        Hovered?.Invoke(now?.Node);
        InvalidateVisual();
        Kick();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (ContextMenu?.IsOpen == true) return; // the menu is about the item it was opened on
        hover = null;
        Hovered?.Invoke(null);
        InvalidateVisual();
        Kick();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (hover is { } h && vanish is null) Entered?.Invoke(blocks[h.Top].Node);
    }
}
