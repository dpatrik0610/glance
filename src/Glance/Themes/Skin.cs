using System.Windows;
using System.Windows.Media;

namespace Glance;

/// The color palette (Themes\Mint.xaml, Flamingo.xaml) under Theme.xaml's styles. Styles use StaticResource,
/// so it's picked once at startup, before anything reads a resource; switching restarts Glance.
static class Skin
{
    public static readonly string[] Names = ["Mint", "Flamingo"];

    public static void Load(string name)
    {
        if (name == "Flamingo")
            Application.Current.Resources.MergedDictionaries[0] = new() { Source = new Uri("pack://application:,,,/Themes/Flamingo.xaml") };
    }

    // Typed lookups for code-built UI. Throws on a misspelled key instead of drawing nothing.
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
    public static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);
    public static Color BrushColor(string key) => ((SolidColorBrush)Brush(key)).Color;

    /// The icon font (Segoe Fluent Icons glyphs).
    public static FontFamily Icons => Font("Icons");

    /// A palette color (PanelColor, SurfaceColor…) with its alpha replaced.
    public static Color Get(string key, byte alpha = 0xFF)
    {
        var c = (Color)Application.Current.Resources[key];
        c.A = alpha;
        return c;
    }
}
