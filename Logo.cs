using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Glance;

/// The Glance mark drawn live (an open orange ring, the white pupil looking out through its gap), so it can move:
/// - Play() draws the ring in and glances left and right; a click pokes it, and enough quick pokes annoy it (> <).
/// - As a loader (Status) the whole mark turns, the pupil following the gap.
/// - As a button icon (Icon="close") it rests as just that icon; hovering its button sweeps the ring around it
///   while the icon folds into the pupil and opens back out.
/// Glance.ico is the same mark on a dark tile (tools/make-icon.ps1).
public sealed class Logo : FrameworkElement
{
    // In a 100×100 box: where the ring's arc starts and how far it runs clockwise (the gap is up and right).
    // A button icon gets a thinner, tighter ring with a bigger pupil inside, and draws past its own bounds (Overdraw).
    const double Start = -20, Arc = 280, ButtonScale = 1.5, ButtonWidths = 1.35, Overdraw = 1.6;
    static readonly Pen MarkPen = RingPen(15), ButtonPen = RingPen(8);
    static readonly IEasingFunction Ease = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    static Pen RingPen(double width) => Frozen(new Pen(new LinearGradientBrush(
        [new(Color.FromRgb(0xFF, 0x9A, 0x6A), 0), new(Color.FromRgb(0xFF, 0x6A, 0x3D), 0.55), new(Color.FromRgb(0xE8, 0x44, 0x2A), 1)],
        new Point(0, 0), new Point(100, 100)) { MappingMode = BrushMappingMode.Absolute }, width)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    double R => Icon is null ? 36 : 33;

    // ---- Pupil shapes. Every shape is the same list of numbers, so any two blend into each other stroke by stroke:
    // its center, one ring (offset, radius, width; radius 0 = a filled dot) and Segs round-capped strokes (ends, width).
    // Unused strokes sit collapsed at the center with width 0, and grow out of it when the next shape needs them.
    const int Segs = 8, Size = 6 + 5 * Segs;

    static double[] Shape(double cx, double cy, (double X, double Y, double R, double W) ring, params (double X1, double Y1, double X2, double Y2, double W)[] segs)
    {
        var s = new double[Size];
        (s[0], s[1], s[2], s[3], s[4], s[5]) = (cx, cy, ring.X, ring.Y, ring.R, ring.W);
        for (var i = 0; i < segs.Length; i++) (s[6 + 5 * i], s[7 + 5 * i], s[8 + 5 * i], s[9 + 5 * i], s[10 + 5 * i]) = segs[i];
        return s;
    }

    static readonly (double, double, double, double) NoRing = (0, 0, 0, 0);
    static readonly double[] Dot = Shape(57, 44, (0, 0, 0, 26)); // at rest: looking up and right, toward the gap

    static readonly Dictionary<string, double[]> Shapes = new()
    {
        ["close"] = Shape(50, 50, NoRing, (-11, -11, 11, 11, 5.5), (11, -11, -11, 11, 5.5)),
        ["menu"] = Shape(50, 50, NoRing, (-12, -9, 12, -9, 5), (-12, 0, 12, 0, 5), (-12, 9, 12, 9, 5)),
        ["search"] = Shape(50, 50, (-3, -3, 8, 5), (3.5, 3.5, 12, 12, 5.5)),
        ["gear"] = Shape(50, 50, (0, 0, 8, 5.5), Enumerable.Range(0, Segs).Select(i =>
        {
            var a = i * Math.PI / 4;
            return (11 * Math.Cos(a), 11 * Math.Sin(a), 15 * Math.Cos(a), 15 * Math.Sin(a), 5.5);
        }).ToArray()),
        // Squeezed-shut eyes, after one poke too many
        ["annoyed"] = Shape(50, 50, NoRing, (-16, -7, -7, 0, 5), (-7, 0, -16, 7, 5), (16, -7, 7, 0, 5), (7, 0, 16, 7, 5)),
    };

    double[] from = Dot, to = Dot;

    public static readonly DependencyProperty SweepProperty = Part(nameof(Sweep), 1);
    public static readonly DependencyProperty PopProperty = Part(nameof(Pop), 1);
    public static readonly DependencyProperty LookXProperty = Part(nameof(LookX), 0);
    public static readonly DependencyProperty LookYProperty = Part(nameof(LookY), 0);
    public static readonly DependencyProperty SpinProperty = Part(nameof(Spin), 0);
    public static readonly DependencyProperty MorphProperty = Part(nameof(Morph), 1);

    /// How much of the ring is drawn (0-1), the pupil's size (0-1), its offset, the turn of the whole mark,
    /// and how far the pupil has blended from its last shape to its next (0-1).
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    public double Pop { get => (double)GetValue(PopProperty); set => SetValue(PopProperty, value); }
    public double LookX { get => (double)GetValue(LookXProperty); set => SetValue(LookXProperty, value); }
    public double LookY { get => (double)GetValue(LookYProperty); set => SetValue(LookYProperty, value); }
    public double Spin { get => (double)GetValue(SpinProperty); set => SetValue(SpinProperty, value); }
    public double Morph { get => (double)GetValue(MorphProperty); set => SetValue(MorphProperty, value); }

    /// Makes this a loader for a status text: shown and turning while the text ends in "…" ("Loading…", "Starting Docker…").
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(string), typeof(Logo),
        new(null, (d, _) => ((Logo)d).UpdateLoader()));
    public string? Status { get => (string?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    bool Busy => Status?.EndsWith('…') == true;

    /// Makes this a button icon (a Shapes name). The nearest Button or ListBoxItem above it is what gets hovered.
    public string? Icon { get; set; }

    double[] Rest => Icon is not null && Shapes.TryGetValue(Icon, out var s) ? s : Dot;
    bool Pokeable => Icon is null && Status is null;
    int gen; // bumps on every morph, so a chained or delayed step that was overtaken doesn't run
    int pokes;
    DateTime lastPoke;
    UIElement? host;

    public Logo()
    {
        IsVisibleChanged += (_, _) => UpdateSpin();
        Loaded += (_, _) =>
        {
            if (Icon is null || host is not null) return;
            from = to = Rest;
            Sweep = 0;
            for (DependencyObject? d = this; d is not null; d = VisualTreeHelper.GetParent(d))
                if (d is ButtonBase or ListBoxItem)
                {
                    host = (UIElement)d;
                    host.MouseEnter += (_, _) => Hover(true);
                    host.MouseLeave += (_, _) => Hover(false);
                    break;
                }
        };
    }

    /// Hovered: the ring sweeps around while the icon folds into the pupil and opens back out. Left: the ring withdraws.
    void Hover(bool on)
    {
        if (!SystemParameters.ClientAreaAnimation) { Sweep = on ? 1 : 0; return; }
        BeginAnimation(SweepProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 620 : 260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (on) MorphTo(Dot, 200, () => MorphTo(Rest, 400));
        else MorphTo(Rest, 220);
    }

    /// A click on the mark: it flinches, a little harder each time; the fifth quick poke annoys it for a moment.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Pokeable) return;
        e.Handled = true; // not a window drag
        var now = DateTime.Now;
        pokes = now - lastPoke < TimeSpan.FromSeconds(2) ? pokes + 1 : 1;
        lastPoke = now;
        if (pokes >= 5)
        {
            pokes = 0;
            MorphTo(Shapes["annoyed"], 220);
            BeginAnimation(SpinProperty, Keys((0, 0), (70, -14), (150, 12), (230, -10), (310, 8), (400, 0)));
            var g = gen;
            var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
            back.Tick += (_, _) => { back.Stop(); if (g == gen) MorphTo(Dot, 320); };
            back.Start();
            return;
        }
        var jolt = 3 + 2.5 * pokes;
        BeginAnimation(PopProperty, Keys((0, 1), (80, 0.72), (280, 1)));
        BeginAnimation(LookXProperty, Keys((0, 0), (80, -jolt), (180, jolt / 2), (320, 0)));
        BeginAnimation(SpinProperty, Keys((0, 0), (70, -jolt), (170, jolt * 0.6), (280, 0)));
    }

    // The whole box takes pokes, not just the drawn strokes
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        Pokeable ? new PointHitTestResult(this, p.HitPoint) : base.HitTestCore(p);

    /// Morphs the pupil into a Shapes icon, or back to the dot (null). Starts from wherever it is now, so quick changes chain smoothly.
    public void Become(string? shape) => MorphTo(shape is not null && Shapes.TryGetValue(shape, out var s) ? s : Dot, 320);

    void MorphTo(double[] next, int ms, Action? then = null)
    {
        var g = ++gen;
        from = Current();
        to = next;
        if (!SystemParameters.ClientAreaAnimation) { BeginAnimation(MorphProperty, null); Morph = 1; InvalidateVisual(); then?.Invoke(); return; }
        var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
        if (then is not null) a.Completed += (_, _) => { if (g == gen) then(); };
        BeginAnimation(MorphProperty, a);
    }

    double[] Current()
    {
        var t = Morph;
        var s = new double[Size];
        for (var i = 0; i < Size; i++) s[i] = from[i] + (to[i] - from[i]) * t;
        return s;
    }

    /// The launch glance: the ring sweeps closed, the pupil pops in, looks left, then right, and settles. Still with reduced motion.
    public void Play()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        BeginAnimation(SweepProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        BeginAnimation(PopProperty, Keys((0, 0), (600, 0), (800, 1.12), (950, 1)));
        BeginAnimation(LookXProperty, Keys((0, -6), (900, -6), (1380, -20), (1690, -20), (2320, 6), (2630, 6), (3200, 0)));
        BeginAnimation(LookYProperty, Keys((0, 5), (2630, 5), (3200, 0)));
    }

    void UpdateLoader()
    {
        Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSpin();
    }

    void UpdateSpin() // only while shown, so hidden loaders cost nothing
    {
        if (Busy && IsVisible)
            BeginAnimation(SpinProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever });
        else if (Status is not null)
            BeginAnimation(SpinProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var box = Math.Min(ActualWidth, ActualHeight) * (Icon is null ? 1 : Overdraw);
        var scale = box / 100;
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - box) / 2, (ActualHeight - box) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushTransform(new RotateTransform(Spin, 50, 50));
        var sweep = Arc * Sweep;
        if (sweep > 0.5)
        {
            var arc = new StreamGeometry();
            using (var c = arc.Open())
            {
                c.BeginFigure(At(Start), false, false);
                c.ArcTo(At(Start + sweep), new System.Windows.Size(R, R), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, Icon is null ? MarkPen : ButtonPen, arc);
        }
        DrawPupil(dc, Current());
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    void DrawPupil(DrawingContext dc, double[] s)
    {
        var k = Pop * (Icon is null ? 1 : ButtonScale);
        var kw = Pop * (Icon is null ? 1 : ButtonWidths); // stroke widths grow less than shapes, so icons stay crisp
        if (k <= 0) return;
        double cx = s[0] + LookX, cy = s[1] + LookY;
        Point P(double x, double y) => new(cx + x * k, cy + y * k);
        var (rho, rw) = (s[4] * k, s[5] * kw);
        if (rw > 0.05)
        {
            if (rho <= rw / 2) dc.DrawEllipse(Brushes.White, null, P(s[2], s[3]), rho + rw / 2, rho + rw / 2); // solid: a dot
            else dc.DrawEllipse(null, new Pen(Brushes.White, rw), P(s[2], s[3]), rho, rho);
        }
        for (var i = 6; i < Size; i += 5)
        {
            var w = s[i + 4] * kw;
            if (w > 0.05)
                dc.DrawLine(new Pen(Brushes.White, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, P(s[i], s[i + 1]), P(s[i + 2], s[i + 3]));
        }
    }

    Point At(double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new(50 + R * Math.Cos(a), 50 + R * Math.Sin(a));
    }

    static DoubleAnimationUsingKeyFrames Keys(params (int Ms, double Value)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (ms, v) in keys) a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Ease));
        return a;
    }

    static DependencyProperty Part(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(Logo), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
