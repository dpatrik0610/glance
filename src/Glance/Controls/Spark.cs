using System.Globalization;
using Glance.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    static readonly Pen DotRing = Frozen(new Pen(new SolidColorBrush(Skin.Get("SurfaceColor")), 2));
    static readonly Brush LabelBg = Frozen(new SolidColorBrush(Skin.Get("SurfaceColor")));
    static readonly Pen LabelEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), 1));
    static readonly Brush LabelText = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5)));
    static readonly Brush LabelSub = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC0)));
    static readonly Typeface Face = new("Segoe UI Variable Text, Segoe UI");
    static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

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

    /// Extra lines under the hover label for point i of a Series (e.g. which apps were behind it); null for none.
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(Func<int, string?>), typeof(Spark));

    public Func<int, string?>? Detail { get => (Func<int, string?>?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public float Max { get => (float)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    // IReadOnlyList, not float[]: the XAML compiler rejects array-typed properties inside templates.
    public IReadOnlyList<float>? Series { get => (IReadOnlyList<float>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public TimeSpan Step { get => (TimeSpan)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public static string Text(string format, float v) => format == "rate" ? Units.Rate(v) : string.Format(CultureInfo.CurrentCulture, format, v);

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

    double Pitch => ActualWidth / (data.Length - (live ? 2 : 1));

    float ValueAt(int i) => data[(head - count + i + data.Length) % data.Length];

    // The vertical scale: 0..Max, or the data's own range (padded) when Max is 0. False when there's nothing to draw.
    bool Scale(out float lo, out float hi)
    {
        (lo, hi) = (0, Max);
        if (count < 2) return false;
        if (hi > 0) return true;
        (lo, hi) = (float.MaxValue, float.MinValue);
        foreach (var v in data)
            if (!float.IsNaN(v)) (lo, hi) = (Math.Min(lo, v), Math.Max(hi, v));
        if (lo > hi) return false; // all gaps
        var pad = Math.Max((hi - lo) * 0.1f, Math.Abs(hi) * 0.001f + 1e-6f);
        (lo, hi) = (lo - pad, hi + pad);
        return true;
    }

    Point At(int i, float v, float lo, float hi) =>
        new(ActualWidth - (count - 1 - i) * Pitch, ActualHeight - Math.Clamp((v - lo) / (hi - lo), 0, 1) * (ActualHeight - 2) - 1);

    /// The point under x (where it's drawn right now, glide included), or null over a gap or outside the data.
    (int I, float V, Point P)? Hit(double x)
    {
        if (!Scale(out var lo, out var hi)) return null;
        var shift = glide.X;
        var i = (int)Math.Round((x - shift - (ActualWidth - (count - 1) * Pitch)) / Pitch);
        if (i < 0 || i >= count || ValueAt(i) is var v && float.IsNaN(v)) return null;
        var p = At(i, v, lo, hi);
        p.X += shift;
        return (i, v, p);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Live: one extra slot hangs off the left edge, so the gliding line never shows a gap there.
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hover anywhere, not just on the line
        if (!Scale(out var lo, out var hi)) return;

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
                var p = At(i, v, lo, hi);
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

        if (hoverX is { } hx) DrawHover(dc, hx);
        label?.InvalidateVisual(); // shows, moves or clears the label with the chart
    }

    // Each layer marks its own point and the bottom layer draws the guide line. The top layer shows a single label for
    // all layers: the time, then each series' value with what was behind it.
    void DrawHover(DrawingContext dc, double hx)
    {
        var layers = Layered.ToList();
        if (Hit(hx) is { } m)
        {
            if (layers[0] == this) dc.DrawLine(Guide, new Point(m.P.X, 0), new Point(m.P.X, ActualHeight));
            dc.DrawEllipse(solid, DotRing, m.P, 4, 4);
        }
        if (layers[^1] != this) return;
        if (label is null && AdornerLayer.GetAdornerLayer(this) is { } adorners) adorners.Add(label = new HoverLabel(this));
        label?.InvalidateVisual();
    }

    HoverLabel? label;

    // The label lives on the adorner layer, above everything in the window: a tall label (several apps per series)
    // would otherwise be painted over by whatever comes after the chart.
    sealed class HoverLabel : Adorner
    {
        readonly Spark chart;

        public HoverLabel(Spark chart) : base(chart) => (this.chart, IsHitTestVisible) = (chart, false);

        protected override void OnRender(DrawingContext dc)
        {
            if (chart.hoverX is { } hx) chart.DrawLabel(dc, hx);
        }
    }

    void DrawLabel(DrawingContext dc, double hx)
    {
        var parts = Layered.Select(s => (s, hit: s.Hit(hx))).Where(x => x.hit is not null).Select(x => (x.s, hit: x.hit!.Value)).ToList();
        if (parts.Count == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Ft(string t, Typeface f, double size, Brush b) => new(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, f, size, b, dpi);
        var (first, at) = (parts[0].s, parts[0].hit);
        var header = first.Step > TimeSpan.Zero
            ? Ft((DateTime.Now - first.Step * (first.count - 1 - at.I)).ToString(first.Step >= TimeSpan.FromHours(1) ? "ddd HH:mm" : "HH:mm"), Face, 11, LabelSub)
            : null;
        var sections = parts.Select(x => (x.s.solid,
            value: Ft(Text(x.s.Format, x.hit.V), Bold, 12, LabelText),
            more: !x.s.live && x.s.Detail?.Invoke(x.hit.I) is { } d ? Ft(d, Face, 11, LabelSub) : null)).ToList();

        const double Pad = 10, Indent = 14, Gap = 6;
        var width = Math.Max(header?.Width ?? 0, sections.Max(x => Indent + Math.Max(x.value.Width, x.more?.Width ?? 0)));
        var height = (header is null ? 0 : header.Height + Gap) + sections.Sum(x => x.value.Height + (x.more is null ? 0 : x.more.Height + 2)) + Gap * (sections.Count - 1);
        var box = new Rect(0, 4, width + Pad * 2, height + Pad * 1.6);
        var ax = at.P.X;
        box.X = ax + 14 + box.Width > ActualWidth ? Math.Max(0, ax - 14 - box.Width) : ax + 14; // flip left near the right edge
        dc.DrawRoundedRectangle(LabelBg, LabelEdge, box, 9, 9);

        var y = box.Y + Pad * 0.8;
        if (header is not null)
        {
            dc.DrawText(header, new Point(box.X + Pad, y));
            y += header.Height + Gap;
        }
        foreach (var (color, value, more) in sections)
        {
            dc.DrawEllipse(color, null, new Point(box.X + Pad + 4, y + value.Height / 2), 3.5, 3.5);
            dc.DrawText(value, new Point(box.X + Pad + Indent, y));
            y += value.Height;
            if (more is not null)
            {
                dc.DrawText(more, new Point(box.X + Pad + Indent, y + 2));
                y += more.Height + 2;
            }
            y += Gap;
        }
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
