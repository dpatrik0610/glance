using System.Windows;
using System.Windows.Media;

namespace Glance;

/// Area chart. Either a rolling buffer (Push) or a whole series (Series). NaN = gap.
/// Renders straight to the DrawingContext: no per-point elements.
public sealed class Spark : FrameworkElement
{
    float[] data = new float[60];
    int count, head;
    Pen pen = null!;
    Brush fill = null!;

    public Spark() => SetBrushes(Colors.White);

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(Spark),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Spark)d).SetBrushes((Color)e.NewValue)));

    /// Fixed top of the scale from 0; 0 = fit the data's own min..max.
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(float), typeof(Spark),
        new FrameworkPropertyMetadata(100f, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<float>), typeof(Spark),
        new FrameworkPropertyMetadata(null, (d, e) => ((Spark)d).Set(e.NewValue as float[] ?? (e.NewValue as IReadOnlyList<float>)?.ToArray() ?? [])));

    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public float Max { get => (float)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    // IReadOnlyList, not float[]: the XAML compiler rejects array-typed properties inside templates.
    public IReadOnlyList<float>? Series { get => (IReadOnlyList<float>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    void SetBrushes(Color c)
    {
        pen = new Pen(new SolidColorBrush(c), 2) { LineJoin = PenLineJoin.Round };
        fill = new LinearGradientBrush(Color.FromArgb(0x50, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        pen.Freeze();
        fill.Freeze();
    }

    public void Push(float? v)
    {
        data[head] = v ?? float.NaN;
        head = (head + 1) % data.Length;
        count = Math.Min(count + 1, data.Length);
        InvalidateVisual();
    }

    void Set(float[] values)
    {
        (data, head, count) = (values, 0, values.Length);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (count < 2) return;
        double w = ActualWidth, h = ActualHeight, step = w / (data.Length - 1);
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

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            Point? last = null;
            for (var i = 0; i < count; i++)
            {
                var v = data[(head - count + i + data.Length) % data.Length];
                if (float.IsNaN(v))
                {
                    CloseArea(a, last, h);
                    last = null;
                    continue;
                }
                var p = new Point(w - (count - 1 - i) * step, h - Math.Clamp((v - lo) / (hi - lo), 0, 1) * (h - 2) - 1);
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
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    static void CloseArea(StreamGeometryContext a, Point? last, double h)
    {
        if (last is { } p) a.LineTo(new Point(p.X, h), false, false);
    }
}
