using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Glance;

/// The Glance mark drawn live (an open orange ring, the white pupil looking out through its gap), so it can move:
/// Play() draws the ring in and glances left and right; as a loader the whole mark turns, the pupil following the gap;
/// and the pupil morphs into the icon of whatever the mouse is over (Logo.Hint="close" on a button).
/// Glance.ico is the same mark on a dark tile (tools/make-icon.ps1).
public sealed class Logo : FrameworkElement
{
    // In a 100×100 box: ring radius and width, where the arc starts and how far it runs clockwise (the gap is up and right).
    const double R = 36, W = 15, Start = -20, Arc = 280;
    static readonly Pen RingPen = Frozen(new Pen(new LinearGradientBrush(
        [new(Color.FromRgb(0xFF, 0x9A, 0x6A), 0), new(Color.FromRgb(0xFF, 0x6A, 0x3D), 0.55), new(Color.FromRgb(0xE8, 0x44, 0x2A), 1)],
        new Point(0, 0), new Point(100, 100)) { MappingMode = BrushMappingMode.Absolute }, W)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly IEasingFunction Ease = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

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

    /// On any element: hovering it morphs the pupil of its window's hint logo (ShowsHints) into this shape.
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(Logo),
        new(null, HintChanged));
    public static string? GetHint(DependencyObject d) => (string?)d.GetValue(HintProperty);
    public static void SetHint(DependencyObject d, string? v) => d.SetValue(HintProperty, v);

    /// This logo answers its window's Hint hovers.
    public bool ShowsHints { get; set; }
    static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached("Host", typeof(Logo), typeof(Logo));

    static void HintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement u || e.OldValue is not null) return; // hook once
        u.MouseEnter += (_, _) => HostOf(u)?.Become(GetHint(u));
        u.MouseLeave += (_, _) => HostOf(u)?.Become(null);
    }

    static Logo? HostOf(DependencyObject d) => Window.GetWindow(d)?.GetValue(HostProperty) as Logo;

    public Logo()
    {
        IsVisibleChanged += (_, _) => UpdateSpin();
        Loaded += (_, _) => { if (ShowsHints) Window.GetWindow(this)?.SetValue(HostProperty, this); };
    }

    /// Morphs the pupil into a Shapes icon, or back to the dot (null). Starts from wherever it is now, so quick hovers chain smoothly.
    public void Become(string? shape)
    {
        var next = shape is not null && Shapes.TryGetValue(shape, out var s) ? s : Dot;
        if (next == to) return;
        from = Current();
        to = next;
        if (!SystemParameters.ClientAreaAnimation) { BeginAnimation(MorphProperty, null); Morph = 1; InvalidateVisual(); return; }
        BeginAnimation(MorphProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
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
        else
            BeginAnimation(SpinProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 100;
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 100 * scale) / 2, (ActualHeight - 100 * scale) / 2));
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
            dc.DrawGeometry(null, RingPen, arc);
        }
        DrawPupil(dc, Current());
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    void DrawPupil(DrawingContext dc, double[] s)
    {
        var k = Pop;
        if (k <= 0) return;
        double cx = s[0] + LookX, cy = s[1] + LookY;
        Point P(double x, double y) => new(cx + x * k, cy + y * k);
        var (rho, rw) = (s[4] * k, s[5] * k);
        if (rw > 0.05)
        {
            if (rho <= rw / 2) dc.DrawEllipse(Brushes.White, null, P(s[2], s[3]), rho + rw / 2, rho + rw / 2); // solid: a dot
            else dc.DrawEllipse(null, new Pen(Brushes.White, rw), P(s[2], s[3]), rho, rho);
        }
        for (var i = 6; i < Size; i += 5)
        {
            var w = s[i + 4] * k;
            if (w > 0.05)
                dc.DrawLine(new Pen(Brushes.White, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, P(s[i], s[i + 1]), P(s[i + 2], s[i + 3]));
        }
    }

    static Point At(double degrees)
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
