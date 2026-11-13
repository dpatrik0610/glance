using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glance;

/// One sidebar page made of related pages, switched with chips along the top. Each part is built on first visit and kept.
public sealed class Tabbed : UserControl
{
    public record Part(string Name, string Icon, Func<UserControl> Create);

    readonly Part[] parts;
    readonly UserControl?[] built;
    readonly RadioButton[] chips;
    readonly ContentControl host = new() { Focusable = false };
    readonly TranslateTransform shift = new();

    public Tabbed(Part[] parts)
    {
        this.parts = parts;
        built = new UserControl?[parts.Length];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 10) };
        chips = parts.Select((p, i) =>
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = p.Icon, FontFamily = (FontFamily)FindResource("Icons"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = p.Name });
            // Same parent panel = one radio group (no GroupName: those are shared window-wide)
            var chip = new RadioButton { Content = label, Style = (Style)FindResource("Chip") };
            System.Windows.Automation.AutomationProperties.SetName(chip, p.Name);
            chip.Checked += (_, _) => Switch(i);
            row.Children.Add(chip);
            return chip;
        }).ToArray();
        host.RenderTransform = shift;
        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Top);
        dock.Children.Add(row);
        dock.Children.Add(host);
        Content = dock;
        chips[0].IsChecked = true;
    }

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
        if (!first) Motion.Rise(host, shift);
    }
}
