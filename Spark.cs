using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using Glance.Pages;

namespace Glance;

/// Area chart. Either a rolling buffer (Push) or a whole series (Series). NaN = gap.
/// Renders straight to the DrawingContext: no per-point elements.
/// Hover shows a guide line and the value under the cursor; charts stacked in the same panel share the hover.
public sealed class Spark : FrameworkElement
{
    static readonly Pen Guide = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1));
    static readonly Pen DotRing = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)), 2));
    static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x1E, 0x22)));
    static readonly Brush LabelText = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5)));
    static readonly Typeface Face = new("Segoe UI Variable Text, Segoe UI");

    float[] data = new float[60];
    int count, head;
    Pen pen = null!;
    Brush fill = null!, solid = null!;
    double? hoverX;
    bool live; // filled by Push: glides between samples
    readonly TranslateTransform glide = new();
    readonly System.Diagnostics.Stopwatch sincePush = new();

    public Spark() => SetBrushes(Colors.White);

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(Spark),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Spark)d).SetBrushes((Color)e.NewValue)));

    /// Fixed top of the scale from 0; 0 = fit the data's own min..max.
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(float), typeof(Spark),
        new FrameworkPropertyMetadata(100f, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<float>), typeof(Spark),
        new FrameworkPropertyMetadata(null, (d, e) => ((Spark)d).Set(e.NewValue as float[] ?? (e.NewValue as IReadOnlyList<float>)?.ToArray() ?? [])));

    /// How the hover label shows a value: a .NET format string ("{0:0}%"), or "rate" for bytes/s.
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(string), typeof(Spark),
        new FrameworkPropertyMetadata("{0:0.#}"));

    /// Time between points, newest = now. When set, the hover label also says when.
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(TimeSpan), typeof(Spark),
        new FrameworkPropertyMetadata(TimeSpan.Zero));

    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public float Max { get => (float)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    // IReadOnlyList, not float[]: the XAML compiler rejects array-typed properties inside templates.
    public IReadOnlyList<float>? Series { get => (IReadOnlyList<float>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public TimeSpan Step { get => (TimeSpan)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public static string Text(string format, float v) => format == "rate" ? SystemPage.Rate(v) : string.Format(CultureInfo.CurrentCulture, format, v);

    void SetBrushes(Color c)
    {
        pen = Frozen(new Pen(new SolidColorBrush(c), 2) { LineJoin = PenLineJoin.Round });
        fill = Frozen(new LinearGradientBrush(Color.FromArgb(0x50, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90));
        solid = Frozen(new SolidColorBrush(c));
    }

    public void Push(float? v)
    {
        data[head] = v ?? float.NaN;
        head = (head + 1) % data.Length;
        count = Math.Min(count + 1, data.Length);
        live = true;
        Glide();
        InvalidateVisual();
    }

    void Set(float[] values)
    {
        (data, head, count) = (values, 0, values.Length);
        live = false;
        glide.BeginAnimation(TranslateTransform.XProperty, null);
        glide.X = 0;
        InvalidateVisual();
    }

    // Instead of jumping a step per sample, the drawing starts one step to the right and slides left until the next
    // sample is due, so the line moves at a steady speed. Only this transform animates: no re-render per frame.
    void Glide()
    {
        var dt = sincePush.Elapsed;
        sincePush.Restart();
        var step = ActualWidth / (data.Length - 2);
        if (count < 2 || step <= 0 || dt < TimeSpan.FromMilliseconds(100) || dt > TimeSpan.FromSeconds(3))
        {
            glide.BeginAnimation(TranslateTransform.XProperty, null); // first sample after a pause: no guess at the pace
            glide.X = 0;
            return;
        }
        // Start from wherever the last glide got to, so jitter in sample timing never makes the line jump.
        var a = new DoubleAnimation(glide.X + step, 0, dt);
        Timeline.SetDesiredFrameRate(a, 30); // a few px per second: 30 fps looks the same and costs half
        glide.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // Charts layered in one panel (CPU over GPU, down over up) hover together.
    IEnumerable<Spark> Layered => Parent is Panel p ? p.Children.OfType<Spark>() : [this];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        foreach (var s in Layered) { s.hoverX = x; s.InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        foreach (var s in Layered) { s.hoverX = null; s.InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Live: one extra slot hangs off the left edge, so the gliding line never shows a gap there.
        double w = ActualWidth, h = ActualHeight, step = w / (data.Length - (live ? 2 : 1));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hover anywhere, not just on the line
        if (count < 2) return;
        float lo = 0, hi = Max;
        if (hi <= 0)
        {
            (lo, hi) = (float.MaxValue, float.MinValue);
            foreach (var v in data)
                if (!float.IsNaN(v)) (lo, hi) = (Math.Min(lo, v), Math.Max(hi, v));
            if (lo > hi) return; // all gaps
            var pad = Math.Max((hi - lo) * 0.1f, Math.Abs(hi) * 0.001f + 1e-6f);
            (lo, hi) = (lo - pad, hi + pad);
        }

        float ValueAt(int i) => data[(head - count + i + data.Length) % data.Length];
        Point At(int i, float v) => new(w - (count - 1 - i) * step, h - Math.Clamp((v - lo) / (hi - lo), 0, 1) * (h - 2) - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            Point? last = null;
            for (var i = 0; i < count; i++)
            {
                var v = ValueAt(i);
                if (float.IsNaN(v))
                {
                    CloseArea(a, last, h);
                    last = null;
                    continue;
                }
                var p = At(i, v);
                if (last is null)
                {
                    l.BeginFigure(p, false, false);
                    a.BeginFigure(new Point(p.X, h), true, true);
                }
                else l.LineTo(p, true, true);
                a.LineTo(p, false, false);
                last = p;
            }
            CloseArea(a, last, h);
        }
        line.Freeze();
        area.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(-1, -2, w + 2, h + 4))); // the incoming point waits off the right edge
        dc.PushTransform(glide);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
        dc.Pop();
        dc.Pop();

        if (hoverX is { } hx) DrawHover(dc, hx, w, h, step, ValueAt, At);
    }

    void DrawHover(DrawingContext dc, double hx, double w, double h, double step, Func<int, float> valueAt, Func<int, float, Point> at)
    {
        var shift = glide.X; // where the glide is right now
        var i = (int)Math.Round((hx - shift - (w - (count - 1) * step)) / step);
        if (i < 0 || i >= count || valueAt(i) is var v && float.IsNaN(v)) return;
        var p = at(i, v);
        p.X += shift;
        var layer = Layered.ToList().IndexOf(this);
        if (layer == 0) dc.DrawLine(Guide, new Point(p.X, 0), new Point(p.X, h));
        dc.DrawEllipse(solid, DotRing, p, 4, 4);

        var text = Text(Format, v);
        if (Step > TimeSpan.Zero)
            text += "  ·  " + (DateTime.Now - Step * (count - 1 - i)).ToString(Step >= TimeSpan.FromHours(1) ? "ddd HH:mm" : "HH:mm");
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, LabelText,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var bw = ft.Width + 24;
        var bh = ft.Height + 6;
        var x = p.X + 10 + bw > w ? p.X - 10 - bw : p.X + 10; // flip left near the right edge
        var box = new Rect(Math.Max(0, x), 2 + layer * (bh + 4), bw, bh);
        dc.DrawRoundedRectangle(LabelBg, null, box, bh / 2, bh / 2);
        dc.DrawEllipse(solid, null, new Point(box.X + 8, box.Y + bh / 2), 2.5, 2.5);
        dc.DrawText(ft, new Point(box.X + 14, box.Y + 3));
    }

    static void CloseArea(StreamGeometryContext a, Point? last, double h)
    {
        if (last is { } p) a.LineTo(new Point(p.X, h), false, false);
    }

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
