using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Glance;

/// Glance's own confirm/info box, in the alert toasts' glass style. Replaces Windows' MessageBox (and its ding).
public partial class Dialog : Window
{
    static readonly Color Danger = Color.FromRgb(0xFF, 0x4D, 0x5E), Accent = Color.FromRgb(0xFF, 0x6A, 0x3D);
    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    bool result, closing;

    /// True when the user picks `action`. Esc, Cancel or closing = false.
    public static bool Confirm(string label, string headline, string detail, string action, bool danger = true) =>
        Open(label, headline, detail, action, danger ? Danger : Accent, true);

    public static void Info(string label, string headline, string detail = "") => Open(label, headline, detail, "OK", Accent, false);

    static bool Open(string label, string headline, string detail, string action, Color c, bool cancel)
    {
        var d = Make(label, headline, detail, action, c, cancel);
        d.ShowDialog();
        return d.result;
    }

    internal static Dialog Make(string label, string headline, string detail, string action, Color c, bool cancel)
    {
        var d = new Dialog();
        // Over Glance when it's open; otherwise (tray menu) in the middle of the screen.
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != d)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        d.Glow.Color = c;
        d.EdgeStop.Color = Color.FromArgb(0xCC, c.R, c.G, c.B);
        d.WashStop.Color = Color.FromArgb(0x30, c.R, c.G, c.B);
        d.Dot.Fill = new SolidColorBrush(c);
        d.OkButton.Background = new SolidColorBrush(c);
        d.Label.Text = string.Join("\u200A", label.ToUpperInvariant().ToCharArray()); // wide, like the toasts
        d.Headline.Text = headline;
        d.Detail.Text = detail;
        d.Detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        d.OkButton.Content = action;
        d.CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        if (!cancel) d.OkButton.IsCancel = true; // Esc dismisses an info box too
        return d;
    }

    Dialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Animate(Root, OpacityProperty, 1, 220);
            Animate(Zoom, ScaleTransform.ScaleXProperty, 1, 260);
            Animate(Zoom, ScaleTransform.ScaleYProperty, 1, 260);
            OkButton.Focus();
        };
    }

    void Ok(object sender, RoutedEventArgs e) => Finish(true);

    void Cancel(object sender, RoutedEventArgs e) => Finish(false);

    void Finish(bool ok)
    {
        if (closing) return;
        (closing, result) = (true, ok);
        Animate(Zoom, ScaleTransform.ScaleXProperty, 0.97, 140);
        Animate(Zoom, ScaleTransform.ScaleYProperty, 0.97, 140);
        Animate(Root, OpacityProperty, 0, 140, Close);
    }

    void Drag(object sender, MouseButtonEventArgs e) => DragMove();

    static void Animate(IAnimatable target, DependencyProperty prop, double to, int ms, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        if (done is not null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }
}
