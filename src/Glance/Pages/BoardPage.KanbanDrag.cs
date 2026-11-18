using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Glance.Services;

namespace Glance.Pages;

/// Dragging a Kanban card: the card folds away, a picture of it shrinks into a small chip that follows the pointer,
/// a gap opens where it would land, and on release the chip flies into that place.
public partial class BoardPage
{
    const double ChipW = 200, ChipH = 40, GapH = 48, Ms = 220;

    /// The columns as drawn, for finding where the pointer is.
    readonly List<(string Name, Border Column, StackPanel List)> kanbanColumns = [];
    KanbanDrag? kdrag;

    sealed class KanbanDrag(BoardItem item, Border card, Border ghost, TranslateTransform shift, RotateTransform tilt)
    {
        public BoardItem Item => item;
        public Border Card => card;
        public Border Ghost => ghost;
        public TranslateTransform Shift => shift;
        public RotateTransform Tilt => tilt;
        public FrameworkElement? Gap;
        public (string Column, int Index)? At;
    }

    static void Animate(IAnimatable target, DependencyProperty p, double to, double ms = Ms, EventHandler? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done is not null) a.Completed += done;
        target.BeginAnimation(p, a);
    }

    /// grab: where the card was pressed, in the card.
    void StartKanbanDrag(BoardItem it, Border card, Point grab)
    {
        var size = card.RenderSize;
        // A picture of the card, to squeeze into the chip while the chip's own label fades in.
        var dpi = VisualTreeHelper.GetDpi(card);
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen()) dc.DrawRectangle(new VisualBrush(card), null, new Rect(size));
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(picture);
        var snap = new Image { Source = bmp, Stretch = Stretch.Fill };

        var glyph = new TextBlock
        {
            Text = it.Kind switch { "image" => "", "link" => "", "stat" => "", _ => "" },
            FontFamily = (FontFamily)FindResource("Icons"), Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 9, 0),
        };
        var label = new DockPanel
        {
            Margin = new(12, 0, 12, 0), Opacity = 0, LastChildFill = true,
            Children = { Docked(glyph, Dock.Left), new TextBlock { Text = ChipText(it), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } },
        };
        var shift = new TranslateTransform(-grab.X, -grab.Y);
        var tilt = new RotateTransform();
        var ghost = new Border
        {
            Width = size.Width, Height = size.Height, CornerRadius = new(12), Background = (Brush)FindResource("Popup"),
            BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new(1.5),
            Child = new Grid { ClipToBounds = true, Children = { snap, label } },
            RenderTransform = new TransformGroup { Children = { tilt, shift } },
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black },
        };
        DragLayer.Children.Add(ghost);
        Animate(ghost, WidthProperty, ChipW);
        Animate(ghost, HeightProperty, ChipH);
        Animate(shift, TranslateTransform.XProperty, -22); // the pointer ends up near the chip's left end
        Animate(shift, TranslateTransform.YProperty, -ChipH / 2);
        Animate(tilt, RotateTransform.AngleProperty, -2.5);
        Fade(snap, 0, 160);
        Fade(label, 1, 260);

        // The card folds away where it was (it keeps the mouse, so the drag continues on it).
        var fold = new ScaleTransform(1, 1);
        card.LayoutTransform = fold;
        Animate(fold, ScaleTransform.ScaleYProperty, 0, 200);
        Fade(card, 0, 140);
        card.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0), TimeSpan.FromMilliseconds(200)));

        kdrag = new(it, card, ghost, shift, tilt);
        card.CaptureMouse();
        MoveKanbanDrag(Mouse.GetPosition(DragLayer));
    }

    /// What the chip says: the title, else the first line of text.
    static string ChipText(BoardItem it) => it.Title.Trim() is { Length: > 0 } t ? t : it.Kind switch
    {
        "image" => "Picture",
        "stat" => it.Text,
        "embed" => NoteStore.Notes.FirstOrDefault(n => n.Id == it.Text)?.DisplayTitle ?? "Note",
        _ => it.Text.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', '+', '>', ' ').Replace("[ ] ", "").Replace("[x] ", "").Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "Empty note",
    };

    void MoveKanbanDrag(Point p)
    {
        if (kdrag is not { } d || kanbanColumns.Count == 0) return;
        Canvas.SetLeft(d.Ghost, p.X);
        Canvas.SetTop(d.Ghost, p.Y);

        // The column under the pointer, or the nearest one.
        double Away((string, Border Column, StackPanel) c)
        {
            var x = c.Column.TranslatePoint(new Point(), DragLayer).X;
            return p.X < x ? x - p.X : Math.Max(0, p.X - x - c.Column.ActualWidth);
        }
        var (name, _, list) = kanbanColumns.MinBy(Away);
        // Its place there: before the first other card whose middle is below the pointer.
        var others = list.Children.OfType<Border>().Where(c => c.Tag is BoardItem o && o != d.Item).ToList();
        var index = others.Count(c => c.TranslatePoint(new Point(0, c.ActualHeight / 2), DragLayer).Y < p.Y);
        if (d.At == (name, index)) return;
        d.At = (name, index);

        // The old gap closes as the new one opens.
        if (d.Gap is { } old) CloseGap(old);
        var accent = ((SolidColorBrush)FindResource("Accent")).Color;
        var gap = new Border
        {
            Height = 0, ClipToBounds = true,
            Child = new Border
            {
                Height = GapH - 8, Margin = new(0, 0, 0, 8), CornerRadius = new(10), VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0x1A, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B)), BorderThickness = new(1.5),
            },
        };
        list.Children.Insert(index < others.Count ? list.Children.IndexOf(others[index]) : list.Children.Count, gap);
        Animate(gap, HeightProperty, GapH, 180);
        d.Gap = gap;
    }

    static void CloseGap(FrameworkElement gap) =>
        Animate(gap, HeightProperty, 0, 160, (_, _) => (gap.Parent as Panel)?.Children.Remove(gap));

    /// drop: put it where the gap is (false: the drag was cancelled; everything goes back).
    void EndKanbanDrag(bool drop)
    {
        if (kdrag is not { } d) return;
        kdrag = null; // first: releasing capture below comes back here through LostMouseCapture
        d.Card.ReleaseMouseCapture();
        var target = d.Gap is { IsLoaded: true } g ? g.TranslatePoint(new Point(), DragLayer) : (Point?)null;
        if (drop && d.At is var (name, index))
        {
            var column = board.Items.Where(i => i != d.Item && ColumnOf(i) == name).ToList();
            board.Items.Remove(d.Item);
            d.Item.Column = name;
            var at = index < column.Count ? board.Items.IndexOf(column[index])
                : board.Items.FindLastIndex(i => ColumnOf(i) == name) + 1 is var n && n > 0 ? n : board.Items.Count;
            board.Items.Insert(at, d.Item);
            Changed();
        }
        DrawKanban();
        if (cards.TryGetValue(d.Item, out var landed)) Appear(landed);

        // The chip flies into place, straightens, and fades as the card shows up there.
        var ghost = d.Ghost;
        if (drop && target is { } t)
        {
            Animate(ghost, Canvas.LeftProperty, t.X, 200);
            Animate(ghost, Canvas.TopProperty, t.Y, 200);
            Animate(d.Shift, TranslateTransform.XProperty, 0, 200);
            Animate(d.Shift, TranslateTransform.YProperty, 0, 200);
        }
        Animate(d.Tilt, RotateTransform.AngleProperty, 0, 200);
        Animate(ghost, OpacityProperty, 0, 220, (_, _) => DragLayer.Children.Remove(ghost));
    }
}
