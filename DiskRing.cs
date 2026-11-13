using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Glance;

/// A ring of coloured slices with a number in the middle; slices and number glide to new values. Busy: a comet circles
/// the ring. Burst: sparks fly off it (something was freed). Pull: sparks fall into it (something was found).
/// Drawn every frame only while something moves.
public sealed class DiskRing : FrameworkElement
{
    static readonly Color Track = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), TextC = Color.FromRgb(0xF4, 0xF4, 0xF5), SubC = Color.FromRgb(0x9C, 0x9C, 0xA3);
    static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// Polar, around the middle: A degrees (0 = top, clockwise), distance going from D0 to D1 (fractions of the radius).
    struct Spark { public double A, D0, D1, Age, Life, Size; public Color C; public bool In; }

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Random rng = new();
    readonly List<Spark> sparks = [];
    Color[] colors = [];
    double[] target = [], shown = [];
    double? whole; // what a full ring stands for; null = the slices' sum
    double wholeShown, number, numberShown, spin, comet, last;
    Func<double, string> format = v => v.ToString("0", CultureInfo.CurrentCulture);
    string caption = "";
    Color numberColor = TextC;
    bool busy, running;

    public DiskRing() => IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Kick(); else Stop(); };

    public bool Busy
    {
        get => busy;
        set { busy = value; Kick(); }
    }

    /// The slices, in order from the top clockwise. Whole: the amount a full ring stands for, so slices can drain.
    public void Set(IReadOnlyList<(Color Color, double Value)> slices, double? whole = null)
    {
        if (slices.Count != shown.Length) Array.Resize(ref shown, slices.Count);
        colors = slices.Select(s => s.Color).ToArray();
        target = slices.Select(s => Math.Max(0, s.Value)).ToArray();
        this.whole = whole;
        Kick();
    }

    /// The number in the middle and the line under it. A new caption counts up from zero; the same one glides.
    public void Show(double value, Func<double, string> format, string caption, Color? color = null)
    {
        if (caption != this.caption) numberShown = 0;
        (number, this.format, this.caption, numberColor) = (value, format, caption, color ?? TextC);
        Kick();
    }

    public void Burst(Color c, int count) => Add(c, count, false);
    public void Pull(Color c, int count) => Add(c, count, true);

    void Add(Color c, int count, bool inward)
    {
        for (var i = 0; i < count && sparks.Count < 400; i++)
            sparks.Add(new Spark
            {
                A = rng.NextDouble() * 360, C = c, In = inward, Size = 1.2 + rng.NextDouble() * 1.8, Life = 0.6 + rng.NextDouble() * 0.6,
                D0 = inward ? 1.35 + rng.NextDouble() * 0.4 : 1, D1 = inward ? 0.62 : 1.25 + rng.NextDouble() * 0.45,
            });
        Kick();
    }

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

    static double Glide(double from, double to, double k) => Math.Abs(to - from) <= Math.Max(1e-6, Math.Abs(to) * 1e-3) ? to : from + (to - from) * k;

    void Frame(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.05, now - last);
        if (dt < 0.004) return; // Rendering can fire more than once a frame
        last = now;
        var k = 1 - Math.Exp(-dt * 6);
        var moving = false;
        for (var i = 0; i < shown.Length; i++)
        {
            shown[i] = Glide(shown[i], target[i], k);
            moving |= shown[i] != target[i];
        }
        var w = whole ?? target.Sum();
        wholeShown = wholeShown <= 0 ? w : Glide(wholeShown, w, k);
        numberShown = Glide(numberShown, number, 1 - Math.Exp(-dt * 5));
        if (busy) spin = (spin + dt * 320) % 360;
        comet = Glide(comet, busy ? 1 : 0, 1 - Math.Exp(-dt * 8));
        for (var i = sparks.Count - 1; i >= 0; i--)
        {
            var s = sparks[i];
            s.Age += dt;
            if (s.Age >= s.Life) sparks.RemoveAt(i); else sparks[i] = s;
        }
        InvalidateVisual();
        if (!moving && !busy && comet == 0 && sparks.Count == 0 && wholeShown == w && numberShown == number) Stop();
    }

    static Point At(Point c, double r, double deg)
    {
        var a = deg * Math.PI / 180;
        return new(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }

    static void Arc(DrawingContext dc, Point c, double r, double from, double sweep, Pen pen)
    {
        if (sweep >= 359.9) { dc.DrawEllipse(null, pen, c, r, r); return; }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(c, r, from), false, false);
            ctx.ArcTo(At(c, r, from + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * c.A, 0, 255), c.R, c.G, c.B);

    static Brush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var thick = size * 0.085;
        var r = size / 2 - thick / 2 - size * 0.04;
        dc.DrawEllipse(null, new Pen(Solid(Track), thick), c, r, r);

        var total = wholeShown > 0 ? wholeShown : shown.Sum();
        if (total > 0)
        {
            var visible = shown.Count(v => v / total * 360 > 0.5);
            var gap = visible > 1 ? 1.6 : 0;
            double at = 0;
            for (var i = 0; i < shown.Length; i++)
            {
                var sweep = Math.Min(360 - at, shown[i] / total * 360);
                if (sweep > 0.5) Arc(dc, c, r, at, Math.Max(0.3, sweep - gap), new Pen(Solid(colors[i]), thick) { StartLineCap = PenLineCap.Flat });
                at += sweep;
            }
        }

        // The comet: a bright head and a tail that fades behind it
        if (comet > 0.01)
            for (var i = 0; i < 16; i++)
                Arc(dc, c, r, spin - (i + 1) * 4.5, 4.6, new Pen(Solid(Color.FromArgb((byte)(230 * comet * Math.Pow(1 - i / 16.0, 1.6)), 0xFF, 0xFF, 0xFF)), thick * 0.5));

        foreach (var s in sparks)
        {
            var t = s.Age / s.Life;
            var move = s.In ? t * t : 1 - (1 - t) * (1 - t);
            var p = At(c, r * (s.D0 + (s.D1 - s.D0) * move), s.A + (s.In ? 40 * t : 0));
            dc.DrawEllipse(Solid(Alpha(s.C, s.In ? Math.Sin(Math.PI * t) : 1 - t)), null, p, s.Size, s.Size);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var big = new FormattedText(format(numberShown), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, Math.Max(14, size * 0.14), Solid(numberColor), dpi)
            { MaxTextWidth = r * 1.7, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        var sub = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Regular, Math.Max(11, size * 0.07), Solid(SubC), dpi)
            { MaxTextWidth = r * 1.6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        var top = c.Y - (big.Height + sub.Height) / 2;
        dc.DrawText(big, new Point(c.X - big.MaxTextWidth / 2, top));
        dc.DrawText(sub, new Point(c.X - sub.MaxTextWidth / 2, top + big.Height));
    }
}
