using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Glance;

/// Glance's own confirm/info box: a card in Glance's panel colours that grows out of the Glance mark. Replaces Windows' MessageBox (and its ding).
public partial class Dialog : Window
{
    static readonly Color Danger = Color.FromRgb(0xFF, 0x4D, 0x5E), Accent = Skin.BrushColor("Accent");

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

        d.EdgeStop.Color = Color.FromArgb(0xCC, c.R, c.G, c.B);
        d.WashStop.Color = Color.FromArgb(0x30, c.R, c.G, c.B);
        d.OkButton.Background = new SolidColorBrush(c);
        d.Label.Text = label;
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
            Open();
            OkButton.Focus();
        };
    }

    const double Disc = 72;
    const int Expand = 420;

    /// The mark draws itself on a small disc, then the disc grows into the card while the mark flies to the header.
    async void Open()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        var slot = MarkSlot.TranslatePoint(new Point(MarkSlot.ActualWidth / 2, MarkSlot.ActualHeight / 2), Root);
        double dx = slot.X - w / 2, dy = slot.Y - h / 2, k = MarkSlot.ActualWidth / Mark.Width;
        if (!SystemParameters.ClientAreaAnimation)
        {
            (Shell.Width, Shell.Height, MarkShift.X, MarkShift.Y, MarkScale.ScaleX, MarkScale.ScaleY) = (w, h, dx, dy, k, k);
            return;
        }
        Body.Opacity = Wash.Opacity = 0;
        Shell.RadiusX = Shell.RadiusY = Disc / 2;
        Motion.To(Shell, WidthProperty, Disc, 260, 0);
        Motion.To(Shell, HeightProperty, Disc, 260, 0);
        Mark.Appear();

        await Task.Delay(500);
        if (closing) return;
        Motion.To(Shell, WidthProperty, w, Expand);
        Motion.To(Shell, HeightProperty, h, Expand);
        Motion.To(Shell, Rectangle.RadiusXProperty, 20, Expand);
        Motion.To(Shell, Rectangle.RadiusYProperty, 20, Expand);
        Motion.To(MarkShift, TranslateTransform.XProperty, dx, Expand);
        Motion.To(MarkShift, TranslateTransform.YProperty, dy, Expand);
        Motion.To(MarkScale, ScaleTransform.ScaleXProperty, k, Expand);
        Motion.To(MarkScale, ScaleTransform.ScaleYProperty, k, Expand);

        await Task.Delay(Expand / 2); // text only once the card is nearly round it
        if (closing) return;
        Motion.Rise(Body, BodyShift, 8, 300);
        Motion.To(Wash, OpacityProperty, 1, 400);
    }

    void Ok(object sender, RoutedEventArgs e) => Finish(true);

    void Cancel(object sender, RoutedEventArgs e) => Finish(false);

    void Finish(bool ok)
    {
        if (closing) return;
        (closing, result) = (true, ok);
        Motion.To(Zoom, ScaleTransform.ScaleXProperty, 0.97, 140);
        Motion.To(Zoom, ScaleTransform.ScaleYProperty, 0.97, 140);
        Motion.To(Root, OpacityProperty, 0, 140, done: Close);
    }

    void Drag(object sender, MouseButtonEventArgs e) => DragMove();
}
