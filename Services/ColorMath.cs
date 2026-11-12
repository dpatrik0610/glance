using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Glance.Services;

/// Color conversions, WCAG contrast and color-theory palettes for the Color page. Hues in degrees, the rest 0..1.
public static partial class ColorMath
{
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// "#f60", "#ff6a3d", "ff6a3d", "rgb(255, 106, 61)" or "255 106 61".
    public static bool TryParse(string s, out Color c)
    {
        c = default;
        s = s.Trim();
        var hex = s.TrimStart('#');
        if (HexDigits().IsMatch(hex) && hex.Length is 3 or 6)
        {
            if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            var v = Convert.ToInt32(hex, 16);
            c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }
        var n = Numbers().Matches(s).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        if (n.Length != 3 || n.Any(x => x > 255)) return false;
        c = Color.FromRgb((byte)n[0], (byte)n[1], (byte)n[2]);
        return true;
    }

    [GeneratedRegex("^[0-9a-fA-F]+$")] private static partial Regex HexDigits();
    [GeneratedRegex(@"\d+")] private static partial Regex Numbers();

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        return (Hue(r, g, b, max, d), max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60;
        var i = (int)Math.Floor(h);
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Color.FromRgb(Byte(r), Byte(g), Byte(b));
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, l = (max + min) / 2;
        return (Hue(r, g, b, max, d), d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1)), l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        var v = l + s * Math.Min(l, 1 - l);
        return FromHsv(h, v == 0 ? 0 : 2 * (1 - l / v), v);
    }

    static double Hue(double r, double g, double b, double max, double d)
    {
        if (d == 0) return 0;
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    static byte Byte(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);

    static double Linear(byte v)
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// OKLCH, the perceptual space CSS uses: lightness 0..1, chroma ~0..0.37, hue in degrees.
    public static (double L, double C, double H) ToOklch(Color c)
    {
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        var C = Math.Sqrt(A * A + B * B);
        return (L, C, C < 1e-4 ? 0 : (Math.Atan2(B, A) * 180 / Math.PI + 360) % 360);
    }

    public static (double C, double M, double Y, double K) ToCmyk(Color c)
    {
        var k = 1 - Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        if (k >= 1) return (0, 0, 0, 1);
        return ((1 - c.R / 255.0 - k) / (1 - k), (1 - c.G / 255.0 - k) / (1 - k), (1 - c.B / 255.0 - k) / (1 - k), k);
    }

    /// WCAG relative luminance and contrast ratio (1..21).
    public static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    public static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// The WCAG grade for text in this contrast: AAA, AA, AA large (≥ 18pt or bold 14pt), or fail.
    public static string Grade(double ratio) => ratio >= 7 ? "AAA" : ratio >= 4.5 ? "AA" : ratio >= 3 ? "AA large" : "Fail";

    /// Labelled values, in the order the page lists them. Each is ready to paste into CSS (CMYK aside).
    public static (string Name, string Value)[] Formats(Color c)
    {
        var inv = CultureInfo.InvariantCulture;
        var (h, s, l) = ToHsl(c);
        var (hh, ss, vv) = ToHsv(c);
        var (ol, oc, oh) = ToOklch(c);
        var (cc, mm, yy, kk) = ToCmyk(c);
        return
        [
            ("HEX", Hex(c)),
            ("RGB", $"rgb({c.R}, {c.G}, {c.B})"),
            ("HSL", string.Format(inv, "hsl({0:0}, {1:0}%, {2:0}%)", h, s * 100, l * 100)),
            ("HSV", string.Format(inv, "hsv({0:0}, {1:0}%, {2:0}%)", hh, ss * 100, vv * 100)),
            ("OKLCH", string.Format(inv, "oklch({0:0.0}% {1:0.000} {2:0.0})", ol * 100, oc, oh)),
            ("CMYK", string.Format(inv, "cmyk({0:0}%, {1:0}%, {2:0}%, {3:0}%)", cc * 100, mm * 100, yy * 100, kk * 100)),
        ];
    }

    /// Color-theory palettes from one base color. Hue turns keep its saturation and lightness (HSL).
    public static (string Name, string Hint, Color[] Colors)[] Palettes(Color c)
    {
        var (h, s, l) = ToHsl(c);
        Color Turn(double deg) => deg == 0 ? c : FromHsl(h + deg, s, l);
        Color Mix(Color to, double t) => Color.FromRgb((byte)Math.Round(c.R + (to.R - c.R) * t), (byte)Math.Round(c.G + (to.G - c.G) * t),
            (byte)Math.Round(c.B + (to.B - c.B) * t));
        return
        [
            ("Complementary", "opposite on the wheel: strongest contrast", [Turn(0), Turn(180)]),
            ("Analogous", "neighbours: calm, low contrast", [Turn(-60), Turn(-30), Turn(0), Turn(30), Turn(60)]),
            ("Triadic", "three evenly spaced: vivid but balanced", [Turn(0), Turn(120), Turn(240)]),
            ("Split complementary", "the complement's two neighbours: contrast with less tension", [Turn(0), Turn(150), Turn(210)]),
            ("Tetradic", "two complementary pairs (square): rich, let one lead", [Turn(0), Turn(90), Turn(180), Turn(270)]),
            ("Monochromatic", "one hue, lighter to darker", [.. new[] { 0.9, 0.75, 0.6, 0.45, 0.3, 0.15 }.Select(x => FromHsl(h, s, x))]),
            ("Tints & shades", "mixed with white, then with black", [Mix(Colors.White, 0.75), Mix(Colors.White, 0.5), Mix(Colors.White, 0.25), c,
                Mix(Colors.Black, 0.25), Mix(Colors.Black, 0.5), Mix(Colors.Black, 0.75)]),
        ];
    }
}
