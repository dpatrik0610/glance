using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glance;

/// One sidebar page made of related pages, switched with chips along the top. Each part is built on first visit and kept.
public sealed class Tabbed : UserControl, IDisposable, IHeader
{
    public record Part(string Name, string Icon, Func<UserControl> Create);

    /// Off in Settings → Pages. The first part is never offered there, so it shares its name with the page safely.
    public static bool Offered(Part p) => Services.Features.On(p.Name);

    readonly Part[] parts;
    readonly UserControl?[] built;
    readonly RadioButton[] chips;
    readonly ContentControl host = new() { Focusable = false };
    readonly ContentControl partBar = new() { Focusable = false, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TranslateTransform shift = new();

    public Tabbed(Part[] parts)
    {
        this.parts = parts;
        built = new UserControl?[parts.Length];
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        chips = parts.Select((p, i) =>
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = p.Icon, FontFamily = Skin.Icons, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = p.Name });
            // Same parent panel = one radio group (no GroupName: those are shared window-wide)
            var chip = new RadioButton { Content = label, Style = Skin.Style("Chip") };
            System.Windows.Automation.AutomationProperties.SetName(chip, p.Name);
            chip.Checked += (_, _) => Switch(i);
            row.Children.Add(chip);
            return chip;
        }).ToArray();
        host.RenderTransform = shift;
        // Shown in the window's title row: the chips, then the open part's own bar
        Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { row, partBar } };
        Content = host;
        chips[0].IsChecked = true;
        Sync();
        App.SettingsChanged += Sync;
    }

    // Parts a setting turned off lose their chip; if one was open, the first part takes its place.
    void Sync()
    {
        for (var i = 0; i < parts.Length; i++)
        {
            var on = Offered(parts[i]);
            chips[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && chips[i].IsChecked == true) chips[0].IsChecked = true;
        }
    }

    public void Dispose()
    {
        App.SettingsChanged -= Sync;
        foreach (var part in built) (part as IDisposable)?.Dispose();
    }

    public FrameworkElement Header { get; }

    public UserControl Current => (UserControl)host.Content;

    public bool Has(string name) => parts.Any(p => p.Name == name);

    /// Switches to the named part and returns it.
    public UserControl Show(string name)
    {
        chips[Array.FindIndex(parts, p => p.Name == name)].IsChecked = true;
        return Current;
    }

    void Switch(int i)
    {
        var first = host.Content is null;
        host.Content = built[i] ??= parts[i].Create();
        partBar.Content = (host.Content as IHeader)?.Header;
        if (!first) Motion.Rise(host, shift);
    }
}
