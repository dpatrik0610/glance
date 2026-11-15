using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;
using Stage = Glance.Services.SpeedRun.Stage;

namespace Glance;

/// The speed test's dial (SpeedRun's state, drawn every frame while on screen). Idle: a start button in the middle with
/// sonar rings. Running: the needle does a warm-up sweep while pinging, then follows the live figure on a stepped scale,
/// the lit arc glows, data streams into the arc (download) or out of it (upload), and three bars under the readout fill
/// per stage. It's a Button only for keyboard, focus and UI Automation; only the middle circle takes clicks.
public sealed class SpeedGauge : Button
{
    static readonly double[] Scale = [0, 10, 25, 50, 100, 250, 500, 1000, 2500]; // Mbps, evenly spaced on the dial
    const double From = 135, Sweep = 270; // degrees clockwise from 3 o'clock: the gap is at the bottom
    const double WarmUp = 1.3; // s, the needle's sweep to the top and back while pinging
    static readonly Color PingC = Color.FromRgb(0x4C, 0xC3, 0x7A), DownC = Color.FromRgb(0x5A, 0xA9, 0xFF),
        UpC = Color.FromRgb(0xB0, 0x7C, 0xFF), Accent = Color.FromRgb(0xE6, 0xA6, 0x5D),
        TextC = Color.FromRgb(0xF4, 0xF4, 0xF5), SubC = Color.FromRgb(0x9C, 0x9C, 0xA3);
    static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    struct Bit { public double A, T, Speed; } // a data streak: where on the lit arc (0..1 of it), how far along, how fast

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Bit[] bits = new Bit[44];
    readonly Random rng = new();
    double last; // clock at the previous frame, s
    double needle, shown; // eased: the needle (0..1 of the dial) and the readout figure
    double face = 1; // 1 = the start button, 0 = the live readout
    double flow, hover; // eased: data streaks on/off; button hover (+1) or press (-1)
    Stage stage = Stage.Down; // the last running stage: its color and label stay while the dial settles
    Point hub;
    double radius;
    bool hot; // pointer over the button

    static SpeedGauge() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SpeedGauge), new FrameworkPropertyMetadata(typeof(SpeedGauge)));

    public SpeedGauge()
    {
        FocusVisualStyle = null; // drawn as a ring round the button instead
        AutomationProperties.SetName(this, "Run speed test");
        for (var i = 0; i < bits.Length; i++) bits[i] = new() { A = rng.NextDouble(), T = rng.NextDouble(), Speed = 0.5 + rng.NextDouble() };
        // Animates only while on screen: CompositionTarget.Rendering is static, so this also keeps it from holding the gauge.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) { CompositionTarget.Rendering -= Frame; return; }
            last = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += Frame;
        };
    }

    /// Where v (Mbps) sits on the dial, 0..1: linear within each step of Scale.
    public static double Fraction(double v)
    {
        v = Math.Max(0, v);
        for (var i = 1; i < Scale.Length; i++)
            if (v <= Scale[i]) return (i - 1 + (v - Scale[i - 1]) / (Scale[i] - Scale[i - 1])) / (Scale.Length - 1);
        return 1;
    }

    static Color Tint(Stage s) => s switch { Stage.Ping => PingC, Stage.Up => UpC, _ => DownC };
    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static double Ease(double rate, double dt) => 1 - Math.Exp(-rate * dt);
    double Age => (DateTime.Now - SpeedRun.Since).TotalSeconds;

    void Frame(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now - last < 0.004) return; // Rendering can fire more than once a frame
        var dt = Math.Min(now - last, 0.1);
        last = now;

        var run = SpeedRun.Now;
        if (SpeedRun.Running && run != stage) { stage = run; shown = 0; }
        var warm = run == Stage.Ping && Age < WarmUp;
        var target = run switch
        {
            Stage.Ping => warm ? Math.Sin(Math.PI * Age / WarmUp) : 0,
            Stage.Down or Stage.Up => Fraction(SpeedRun.Live),
            _ => 0,
        };
        needle += (target - needle) * (warm ? 1 : Ease(5, dt)); // the sweep is already smooth
        shown += (SpeedRun.Live - shown) * Ease(7, dt);
        face += ((SpeedRun.Running ? 0 : 1) - face) * Ease(8, dt);
        flow += ((run is Stage.Down or Stage.Up ? 1 : 0) - flow) * Ease(4, dt);
        hover += ((IsPressed ? -1 : hot && !SpeedRun.Running ? 1 : 0) - hover) * Ease(16, dt);
        for (var i = 0; i < bits.Length; i++)
        {
            ref var b = ref bits[i];
            b.T += dt * b.Speed * (0.6 + 2.4 * needle); // faster line, faster stream
            if (b.T < 1) continue;
            (b.T, b.A, b.Speed) = (b.T - 1, rng.NextDouble(), 0.5 + rng.NextDouble());
        }
        InvalidateVisual();
    }

    Point At(double r, double f)
    {
        var a = (From + Sweep * f) * Math.PI / 180;
        return new(hub.X + r * Math.Cos(a), hub.Y + r * Math.Sin(a));
    }

    StreamGeometry Arc(double r, double f0, double f1)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(r, f0), false, false);
            ctx.ArcTo(At(r, f1), new Size(r, r), 0, (f1 - f0) * Sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static Pen Stroke(Brush b, double w, PenLineCap cap = PenLineCap.Round) => new(b, w) { StartLineCap = cap, EndLineCap = cap };

    void Label(DrawingContext dc, string s, double size, Color c, Point at, bool bold = true)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, Math.Max(size, 9),
            new SolidColorBrush(c), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(t, new Point(at.X - t.Width / 2, at.Y - t.Height / 2));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hover anywhere
        // Above the hub: the arc and the streaks outside it (1.25 R). Below: the stage bars and their labels (0.95 R).
        var R = radius = Math.Min(w / 2 / 1.25, h / 2.2);
        if (R < 30) return;
        hub = new Point(w / 2, (h - 2.2 * R) / 2 + 1.25 * R);
        var now = clock.Elapsed.TotalSeconds;
        var tint = Tint(stage);
        var sw = R * 0.085; // arc thickness

        // Bezel and track
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(TextC, 0.05)), 1), hub, R * 1.13, R * 1.13);
        dc.DrawGeometry(null, Stroke(new SolidColorBrush(A(TextC, 0.08)), sw), Arc(R, 0, 1));

        // Ticks: a major one per step of the scale (labelled when there's room), four minor between; lit once passed
        var n = Scale.Length - 1;
        for (var i = 0; i <= n * 5; i++)
        {
            var f = i / (n * 5.0);
            var major = i % 5 == 0;
            var lit = f <= needle + 1e-6 && needle > 0.002;
            var c = lit ? A(tint, major ? 1 : 0.8) : A(SubC, major ? 0.55 : 0.25);
            dc.DrawLine(new Pen(new SolidColorBrush(c), major ? Math.Max(1.5, R * 0.014) : 1), At(R * 0.9, f), At(R * (major ? 0.83 : 0.865), f));
            if (major && R >= 70)
            {
                var v = Scale[i / 5];
                Label(dc, v >= 1000 ? $"{v / 1000:0.#}G" : $"{v:0}", R * 0.075, lit ? TextC : A(SubC, 0.8), At(R * 0.72, f), bold: false);
            }
        }

        // Ping: a comet runs along the track
        if (SpeedRun.Now == Stage.Ping)
        {
            var head = Age * 0.7 % 1.2; // runs past the end, so it leaves before coming round again
            for (var j = 0; j < 18; j++)
            {
                double f1 = Math.Min(head - j * 0.018, 1), f0 = Math.Max(f1 - 0.02, 0);
                if (f1 <= f0) continue;
                dc.DrawGeometry(null, Stroke(new SolidColorBrush(A(PingC, 0.9 * (1 - j / 18.0))), sw, PenLineCap.Flat), Arc(R, f0, f1));
            }
        }

        // The lit arc: two glow passes, then a gradient that brightens toward the needle
        if (needle > 0.002)
        {
            var arc = Arc(R, 0, needle);
            dc.DrawGeometry(null, Stroke(new SolidColorBrush(A(tint, 0.10)), sw * 2.8), arc);
            dc.DrawGeometry(null, Stroke(new SolidColorBrush(A(tint, 0.22)), sw * 1.7), arc);
            var grad = new LinearGradientBrush(A(tint, 0.35), tint, 0)
                { MappingMode = BrushMappingMode.Absolute, StartPoint = At(R, 0), EndPoint = At(R, needle) };
            dc.DrawGeometry(null, Stroke(grad, sw), arc);
        }

        // Data streaks outside the arc: falling in for download, flying out for upload
        if (flow > 0.01 && needle > 0.01)
        {
            var inward = stage == Stage.Down;
            foreach (var b in bits)
            {
                var f = b.A * needle;
                var t = inward ? 1 - b.T : b.T;
                var r = R * (1.07 + 0.2 * t);
                var tail = R * 0.06 * (inward ? 1 : -1); // trails behind its motion
                var pen = Stroke(new SolidColorBrush(A(tint, flow * Math.Sin(Math.PI * b.T) * 0.85)), Math.Max(1.2, R * 0.013));
                dc.DrawLine(pen, At(r, f), At(r + tail, f));
            }
        }

        // The needle: a floating wedge from mid-dial to the arc, fading in toward its tip, and a glowing tip on the arc
        if (needle > 0.002 || SpeedRun.Running)
        {
            Point tip = At(R * 0.93, needle), baseC = At(R * 0.5, needle);
            var ang = (From + Sweep * needle) * Math.PI / 180;
            var side = new Vector(-Math.Sin(ang), Math.Cos(ang)) * R * 0.035;
            var wedge = new StreamGeometry();
            using (var ctx = wedge.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(baseC + side, true, false);
                ctx.LineTo(baseC - side, true, false);
            }
            wedge.Freeze();
            dc.DrawGeometry(new LinearGradientBrush(A(tint, 0), TextC, 0) { MappingMode = BrushMappingMode.Absolute, StartPoint = baseC, EndPoint = tip },
                null, wedge);
            var dot = At(R, needle);
            var glow = new RadialGradientBrush(A(tint, 0.7), A(tint, 0));
            dc.DrawEllipse(glow, null, dot, sw * 1.8, sw * 1.8);
            dc.DrawEllipse(new SolidColorBrush(TextC), null, dot, sw * 0.32, sw * 0.32);
        }

        // Stage bars: ping, download, upload; done ones full, the running one filling over its stage
        Stage[] stages = [Stage.Ping, Stage.Down, Stage.Up];
        double?[] results = [SpeedRun.Ping, SpeedRun.Down, SpeedRun.Up];
        string[] names = ["PING", "DOWN", "UP"];
        double bw = R * 0.24, gap = R * 0.07, bh = Math.Max(3, R * 0.028), y = hub.Y + R * 0.8;
        for (var i = 0; i < 3; i++)
        {
            var x = hub.X - 1.5 * bw - gap + i * (bw + gap);
            var fill = results[i] is not null && SpeedRun.Now != stages[i] ? 1
                : SpeedRun.Now == stages[i] ? Math.Min(Age / (i == 0 ? WarmUp + 0.4 : SpeedRun.PhaseSeconds), 1) : 0;
            var c = Tint(stages[i]);
            dc.DrawRoundedRectangle(new SolidColorBrush(A(TextC, 0.08)), null, new Rect(x, y, bw, bh), bh / 2, bh / 2);
            if (fill > 0) dc.DrawRoundedRectangle(new SolidColorBrush(c), null, new Rect(x, y, Math.Max(bw * fill, bh), bh), bh / 2, bh / 2);
            if (R >= 90) Label(dc, names[i], R * 0.06, fill > 0 ? c : A(SubC, 0.7), new Point(x + bw / 2, y + bh + R * 0.07));
        }

        // The middle: the live readout, crossfading with the start button
        if (face < 0.99)
        {
            dc.PushOpacity(1 - face);
            var ping = stage == Stage.Ping;
            Label(dc, ping ? "PING" : stage == Stage.Up ? "↑  UPLOAD" : "↓  DOWNLOAD", R * 0.075, tint, new Point(hub.X, hub.Y - R * 0.3));
            Label(dc, ping ? $"{shown:0}" : SpeedRun.Mbps(shown), R * 0.36, TextC, hub);
            Label(dc, ping ? "ms" : "Mbps", R * 0.085, SubC, new Point(hub.X, hub.Y + R * 0.28), bold: false);
            dc.Pop();
        }
        if (face > 0.01)
        {
            dc.PushOpacity(face);
            dc.PushTransform(new ScaleTransform(0.8 + 0.2 * face + 0.04 * hover, 0.8 + 0.2 * face + 0.04 * hover, hub.X, hub.Y));
            var rb = R * 0.4;
            // Sonar: two rings rolling out from the button
            for (var k = 0; k < 2; k++)
            {
                var p = (now / 2.6 + k * 0.5) % 1;
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(Accent, 0.45 * (1 - p) * (1 - p))), 1.2), hub, rb + R * 0.22 * p, rb + R * 0.22 * p);
            }
            // A slow dashed ring turning round it
            dc.PushTransform(new RotateTransform(now * 18 % 360, hub.X, hub.Y));
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(Accent, 0.35)), 1) { DashStyle = new DashStyle([2, 5], 0) }, hub, rb * 1.12, rb * 1.12);
            dc.Pop();
            dc.DrawEllipse(new SolidColorBrush(A(Accent, 0.14 + 0.08 * hover)), null, hub, rb + 6, rb + 6); // halo
            dc.DrawEllipse(new RadialGradientBrush(A(Accent, 0.32 + 0.1 * hover), A(Accent, 0.12)) { GradientOrigin = new Point(0.5, 0.25) },
                new Pen(new SolidColorBrush(Accent), Math.Max(1.5, R * 0.016)), hub, rb, rb);
            if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(TextC, 0.7)), 1.5), hub, rb + 10, rb + 10);
            var word = SpeedRun.Now switch { Stage.Done => "AGAIN", Stage.Failed => "RETRY", _ => "START" };
            Label(dc, word, R * 0.12, TextC, hub);
            dc.Pop();
            dc.Pop();
        }
    }

    // ---- Only the middle circle is the button ----

    bool OnButton(Point p) => !SpeedRun.Running && radius > 0 && (p - hub).Length <= radius * 0.46;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        hot = OnButton(e.GetPosition(this));
        Cursor = hot ? Cursors.Hand : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hot = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (OnButton(e.GetPosition(this))) base.OnMouseLeftButtonDown(e);
    }

    protected override void OnClick()
    {
        base.OnClick();
        SpeedRun.Start(); // no-op while one runs
    }
}
