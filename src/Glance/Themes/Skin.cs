using System.Windows;
using System.Windows.Media;

namespace Glance;

/// The color palette (Themes\Mint.xaml, Vice.xaml) under Theme.xaml's styles. Styles use StaticResource,
/// so it's picked once at startup, before anything reads a resource; switching restarts Glance.
static class Skin
{
    public static readonly string[] Names = ["Mint", "Vice City"];

    public static void Load(string name)
    {
        if (name == "Vice City")
            Application.Current.Resources.MergedDictionaries[0] = new() { Source = new Uri("pack://application:,,,/Themes/Vice.xaml") };
    }

    /// A palette color (PanelColor, SurfaceColor…) with its alpha replaced.
    public static Color Get(string key, byte alpha = 0xFF)
    {
        var c = (Color)Application.Current.Resources[key];
        c.A = alpha;
        return c;
    }
}
