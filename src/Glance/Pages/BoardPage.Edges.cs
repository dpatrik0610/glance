using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Glance.Services;

namespace Glance.Pages;

/// Arrows between nodes, and the pen layer.
public partial class BoardPage
{
    void Connect(BoardItem from, BoardItem to)
    {
        if (board.Edges.Any(e => e.From == from.Id && e.To == to.Id)) return;
        board.Edges.Add(new() { From = from.Id, To = to.Id });
        Changed();
    }

    /// Where the line from a box's center toward a point leaves the box.
    internal static Point Exit(Rect r, Point toward)
    {
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var d = toward - c;
        if (d.X == 0 && d.Y == 0) return c;
        var sx = d.X != 0 ? r.Width / 2 / Math.Abs(d.X) : double.PositiveInfinity;
        var sy = d.Y != 0 ? r.Height / 2 / Math.Abs(d.Y) : double.PositiveInfinity;
        return c + d * Math.Min(1, Math.Min(sx, sy));
    }

    void DrawEdges()
    {
        EdgeLayer.Children.Clear();
        var byId = board.Items.ToDictionary(i => i.Id);
        var line = new SolidColorBrush(((SolidColorBrush)FindResource("Sub")).Color) { Opacity = 0.85 };
        foreach (var edge in board.Edges)
        {
            if (!byId.TryGetValue(edge.From, out var from) || !byId.TryGetValue(edge.To, out var to)) continue;
            Rect a = Bounds(from), b = Bounds(to);
            var p1 = Exit(a, new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            var p2 = Exit(b, new Point(a.X + a.Width / 2, a.Y + a.Height / 2));
            if ((p2 - p1).Length < 4) continue; // overlapping nodes
            var dir = p2 - p1;
            dir.Normalize();
            var back = p2 - dir * 11;
            var side = new Vector(-dir.Y, dir.X) * 5;
            var geometry = new PathGeometry([
                new PathFigure(p1, [new LineSegment(back, true)], false),
                new PathFigure(p2, [new LineSegment(back + side, true), new LineSegment(back - side, true)], true),
            ]);
            EdgeLayer.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = line, Fill = line, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });
            // A wide invisible line to click: double-click labels it, right-click for more.
            var hit = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(p1, p2), Stroke = Brushes.Transparent, StrokeThickness = 14, Cursor = Cursors.Hand,
                ToolTip = "Double-click to label, right-click for more",
            };
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            hit.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (e.ClickCount == 2) EditLabel(edge, mid);
            };
            hit.MouseRightButtonDown += (_, e) => e.Handled = true;
            hit.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var m = new ContextMenu { Placement = PlacementMode.MousePoint };
                Item(m, edge.Label == "" ? "Add a label" : "Edit the label", () => EditLabel(edge, mid));
                Item(m, "Reverse", () => { (edge.From, edge.To) = (edge.To, edge.From); Changed(); });
                Item(m, "Delete", () => { board.Edges.Remove(edge); Changed(); });
                m.IsOpen = true;
            };
            EdgeLayer.Children.Add(hit);
            if (edge.Label.Trim() != "")
            {
                var label = new Border
                {
                    Background = (Brush)FindResource("Popup"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1),
                    CornerRadius = new(8), Padding = new(8, 2, 8, 3), IsHitTestVisible = false,
                    Child = new TextBlock { Text = edge.Label, FontSize = 12.5 },
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, mid.X - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, mid.Y - label.DesiredSize.Height / 2);
                EdgeLayer.Children.Add(label);
            }
        }
    }

    /// A box at the arrow's middle; Enter or clicking away keeps the text, Esc doesn't.
    void EditLabel(BoardEdge edge, Point mid)
    {
        var box = new TextBox { Text = edge.Label, MinWidth = 120, Tag = "Label" };
        Canvas.SetLeft(box, mid.X - 60);
        Canvas.SetTop(box, mid.Y - 16);
        Panel.SetZIndex(box, 20);
        World.Children.Add(box);
        var done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            if (keep) edge.Label = box.Text.Trim();
            World.Children.Remove(box);
            Changed();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Finish(e.Key == Key.Enter); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        FocusWhenVisible(box);
        box.SelectAll();
    }

    // ---- pen layer: an InkCanvas inside World, so strokes pan and zoom with the board ----

    void PenClick(object sender, RoutedEventArgs e) =>
        SetPen(sender == PenButton ? (PenButton.IsChecked == true ? InkCanvasEditingMode.Ink : null)
            : EraserButton.IsChecked == true ? InkCanvasEditingMode.EraseByStroke : null);

    /// null: off (the board takes the mouse again).
    void SetPen(InkCanvasEditingMode? mode)
    {
        PenButton.IsChecked = mode == InkCanvasEditingMode.Ink;
        EraserButton.IsChecked = mode == InkCanvasEditingMode.EraseByStroke;
        Ink.EditingMode = mode ?? InkCanvasEditingMode.None;
        Ink.Background = mode is null ? null : Brushes.Transparent;
        Ink.IsHitTestVisible = mode is not null; // otherwise clicks on a stroke reach the node under it
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = ((SolidColorBrush)FindResource("Accent")).Color, Width = 3, Height = 3, FitToCurve = true,
        };
        Surface.Cursor = mode is null ? null : Cursors.Pen;
    }

    void LoadInk()
    {
        Ink.Strokes = new StrokeCollection();
        if (board.Ink == "") return;
        try
        {
            using var s = new MemoryStream(Convert.FromBase64String(board.Ink));
            Ink.Strokes = new StrokeCollection(s);
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { } // unreadable: start clean (saved over on the next stroke)
    }

    void SaveInk()
    {
        if (Ink.Strokes.Count == 0) board.Ink = "";
        else
        {
            using var s = new MemoryStream();
            Ink.Strokes.Save(s);
            board.Ink = Convert.ToBase64String(s.ToArray());
        }
        BoardStore.SaveSoon();
        Track();
    }
}
