using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Glance.Services;

namespace Glance;

/// One sticky on the desktop. Always on top, out of the taskbar and Alt+Tab; remembers where it was put.
public partial class StickyWindow : Window
{
    readonly Sticky sticky;
    IntPtr hwnd;

    public StickyWindow(Sticky s)
    {
        InitializeComponent();
        sticky = s;
        DataContext = s;
        Place();
        Paint();
        s.PropertyChanged += Changed;
        App.SettingsChanged += Blur;
        Closed += (_, _) => { s.PropertyChanged -= Changed; App.SettingsChanged -= Blur; };
        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        Native.RoundedDarkFrame(hwnd);
        Native.ToolWindow(hwnd);
        HwndSource.FromHwnd(hwnd)!.AddHook(Native.KeepFrameActive);
        Blur();
    }

    void Blur() => Native.SetBlur(hwnd, App.Settings.Blur);

    // Where it was left; a new one (or one whose screen is gone) goes near the top-right corner.
    void Place()
    {
        (Width, Height) = (sticky.Width, sticky.Height);
        var area = SystemParameters.WorkArea;
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (sticky.Left is { } l && sticky.Top is { } t && screens.IntersectsWith(new Rect(l, t, 80, 40))) (Left, Top) = (l, t);
        else
        {
            var k = Stickies.Items.IndexOf(sticky) % 6; // cascade, so new ones don't hide each other
            (Left, Top) = (area.Right - Width - 24 - k * 28, area.Top + 72 + k * 28);
        }
    }

    void Remember()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        (sticky.Left, sticky.Top, sticky.Width, sticky.Height) = (Left, Top, Width, Height);
    }

    void Changed(object? o, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Sticky.Color) or nameof(Sticky.Opacity)) Paint();
    }

    void Paint()
    {
        var tint = (Color)ColorConverter.ConvertFromString(sticky.Palette.Tint);
        Tint.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(sticky.Opacity, 0.3, 1) * 255), tint.R, tint.G, tint.B));
        Dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sticky.Dot));
    }

    void DragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void HideClick(object sender, RoutedEventArgs e) => sticky.Shown = false;

    // The dot's menu: the six tints and how see-through it is.
    void DotClick(object sender, RoutedEventArgs e)
    {
        var colors = new WrapPanel { Margin = new(0, 0, 0, 10) };
        foreach (var (name, _, dot) in Sticky.Colors)
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Padding = new(5), ToolTip = name };
            b.Content = new Ellipse
            {
                Width = 16, Height = 16, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dot)),
                Stroke = name == sticky.Color ? Brushes.White : null, StrokeThickness = 2,
            };
            b.Click += (_, _) => sticky.Color = name;
            colors.Children.Add(b);
        }
        var see = new Slider { Minimum = 0.3, Maximum = 1, Value = sticky.Opacity, Width = 150 };
        see.ValueChanged += (_, v) => sticky.Opacity = Math.Round(v.NewValue, 2);
        var popup = new Popup
        {
            PlacementTarget = DotButton, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x1E, 0x1E, 0x22)), CornerRadius = new(12), Padding = new(12),
                BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1),
                Child = new StackPanel
                {
                    Children = { colors, new TextBlock { Text = "Solid", Style = (Style)FindResource("Label"), Margin = new(0, 0, 0, 4) }, see },
                },
            },
        };
        popup.IsOpen = true;
    }
}
