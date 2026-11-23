using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

public partial class ColorPage : UserControl
{
    public record Row(string Name, string Value);

    // HSV is the source of truth while picking: a grey has no hue of its own, and dragging through one mustn't lose it.
    double hue, sat, val;
    Color color;

    public ColorPage()
    {
        InitializeComponent();
        Sv.SizeChanged += (_, _) => { SvClip.Rect = new Rect(Sv.RenderSize); Place(); };
        HueBar.SizeChanged += (_, _) => Place();
        Use(App.Settings.RecentColors.Select(Parse).FirstOrDefault(c => c is not null) ?? Color.FromRgb(0xFF, 0x6A, 0x3D));
        ShowRecent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ShowRecent(); }; // picks from the bar land in Recent too
    }

    static Color? Parse(string s) => ColorMath.TryParse(s, out var c) ? c : null;

    /// Shows this color everywhere on the page; remember: also put it first in Recent.
    public void Use(Color c, bool remember = false)
    {
        (hue, sat, val) = ColorMath.ToHsv(c);
        color = c;
        Show();
        if (remember) Keep();
    }

    void FromHsv()
    {
        color = ColorMath.FromHsv(hue, sat, val);
        Show();
    }

    void Show()
    {
        HueFill.Fill = new SolidColorBrush(ColorMath.FromHsv(hue, 1, 1));
        Swatch.Background = new SolidColorBrush(color);
        if (!HexBox.IsKeyboardFocused) HexBox.Text = ColorMath.Hex(color);
        Place();
        Values.ItemsSource = ColorMath.Formats(color).Select(f => new Row(f.Name, f.Value)).ToArray();

        Contrast.Children.Clear();
        foreach (var (name, bg) in new[] { ("on white", Colors.White), ("on black", Colors.Black) })
        {
            var ratio = ColorMath.Contrast(color, bg);
            var grade = ColorMath.Grade(ratio);
            Contrast.Children.Add(new Border
            {
                Background = new SolidColorBrush(bg), CornerRadius = new(10), Padding = new(14, 10, 14, 10), Margin = new(0, 0, 8, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Aa  Sample text", Foreground = new SolidColorBrush(color), FontSize = 18, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = $"{ratio:0.00}:1 · {grade} · {name}", FontSize = 11.5, Margin = new(0, 4, 0, 0),
                            Foreground = new SolidColorBrush(bg == Colors.White ? Color.FromRgb(0x44, 0x44, 0x48) : Color.FromRgb(0xBB, 0xBB, 0xC0)) },
                    },
                },
            });
        }

        Palettes.Children.Clear();
        foreach (var (name, hint, colors) in ColorMath.Palettes(color))
        {
            var row = new WrapPanel { Margin = new(0, 4, 0, 10) };
            foreach (var c in colors) row.Children.Add(PaletteSwatch(c));
            Palettes.Children.Add(new TextBlock
            {
                Inlines = { new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold },
                    new System.Windows.Documents.Run("  ·  " + hint) { Foreground = Skin.Brush("Sub") } },
                Margin = new(0, 8, 0, 0),
            });
            Palettes.Children.Add(row);
        }
    }

    FrameworkElement PaletteSwatch(Color c)
    {
        var hex = ColorMath.Hex(c);
        var cell = new StackPanel { Margin = new(0, 0, 8, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = hex };
        cell.Children.Add(new Border
        {
            Width = 72, Height = 40, CornerRadius = new(8), Background = new SolidColorBrush(c),
            BorderBrush = Skin.Brush("Line"), BorderThickness = new(1),
        });
        cell.Children.Add(new TextBlock { Text = hex, FontFamily = Skin.Font("Mono"), FontSize = 11, Margin = new(2, 4, 0, 0),
            Foreground = Skin.Brush("Sub") });
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Use(c, remember: true);
            else Copy(hex);
        };
        return cell;
    }

    void Place()
    {
        Canvas.SetLeft(SvThumb, sat * Sv.ActualWidth - 8);
        Canvas.SetTop(SvThumb, (1 - val) * Sv.ActualHeight - 8);
        SvThumb.Fill = new SolidColorBrush(color);
        Canvas.SetLeft(HueThumb, hue / 360 * HueBar.ActualWidth - 9);
        HueThumb.Fill = new SolidColorBrush(ColorMath.FromHsv(hue, 1, 1));
    }

    void SvDown(object sender, MouseButtonEventArgs e)
    {
        Sv.CaptureMouse();
        SvMove(sender, e);
    }

    void SvMove(object sender, MouseEventArgs e)
    {
        if (!Sv.IsMouseCaptured) return;
        var p = e.GetPosition(Sv);
        sat = Math.Clamp(p.X / Sv.ActualWidth, 0, 1);
        val = 1 - Math.Clamp(p.Y / Sv.ActualHeight, 0, 1);
        FromHsv();
    }

    void HueDown(object sender, MouseButtonEventArgs e)
    {
        HueBar.CaptureMouse();
        HueMove(sender, e);
    }

    void HueMove(object sender, MouseEventArgs e)
    {
        if (!HueBar.IsMouseCaptured) return;
        hue = Math.Clamp(e.GetPosition(HueBar).X / HueBar.ActualWidth, 0, 0.9999) * 360;
        FromHsv();
    }

    void DragEnd(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
        Keep();
    }

    void HexKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        HexCommit(sender, e);
        Keyboard.ClearFocus();
    }

    void HexCommit(object sender, RoutedEventArgs e)
    {
        if (ColorMath.TryParse(HexBox.Text, out var c)) Use(c, remember: true);
        HexBox.Text = ColorMath.Hex(color); // bad input snaps back
    }

    async void PickClick(object sender, RoutedEventArgs e)
    {
        Status.Text = "Click anywhere on screen · Esc cancels";
        var c = await Eyedropper.Pick();
        Status.Text = "";
        if (c is { } picked) Use(picked, remember: true);
    }

    void CopyValue(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Row r) Copy(r.Value);
    }

    void Copy(string text)
    {
        Status.Text = ClipboardHistory.Copy(text) ? "Copied " + text : "Clipboard busy, try again.";
        Keep();
    }

    void CopyHex(object sender, RoutedEventArgs e) => Copy(ColorMath.Hex(color));

    void Keep()
    {
        Remember(color);
        ShowRecent();
    }

    /// First in Recent (the last 12), wherever it was picked.
    public static void Remember(Color c)
    {
        var hex = ColorMath.Hex(c);
        var list = App.Settings.RecentColors;
        if (list.FirstOrDefault() == hex) return;
        list.Remove(hex);
        list.Insert(0, hex);
        if (list.Count > 12) list.RemoveRange(12, list.Count - 12);
        App.ApplySettings();
    }

    void ShowRecent()
    {
        Recent.Children.Clear();
        RecentLabel.Visibility = App.Settings.RecentColors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var hex in App.Settings.RecentColors)
        {
            if (Parse(hex) is not { } c) continue;
            var dot = new Border
            {
                Width = 28, Height = 28, CornerRadius = new(8), Margin = new(0, 0, 6, 6), Background = new SolidColorBrush(c),
                BorderBrush = Skin.Brush("Line"), BorderThickness = new(1), Cursor = Cursors.Hand, ToolTip = hex,
            };
            dot.MouseLeftButtonUp += (_, _) => Use(c);
            Recent.Children.Add(dot);
        }
    }
}
