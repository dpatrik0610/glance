using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Glance.Services;

namespace Glance;

/// What an alert shows: Label (dim), Headline + Unit (bright), Detail (dim), then SideTitle + SideText at the end.
/// Sticky alerts stay until clicked or closed; others drain away (paused while hovered). Art: a small picture instead of the dot.
/// Quiet: no chime (song changes: it would play over the music).
public record Toast(string Label, string Headline, string Unit, string SideTitle, string SideText, Color Color, bool Sticky,
    ImageSource? Art = null, string Detail = "", bool Quiet = false);

/// Alerts as pills like the top bar's (same look, size and opacity), stacked downward under it.
public partial class AlertWindow : Window
{
    static readonly List<AlertWindow> open = [];
    static readonly TimeSpan Life = TimeSpan.FromSeconds(8);
    const double Gap = 6;

    readonly Action onClick;
    readonly int slot;
    readonly Storyboard countdown = new();
    readonly bool sticky;
    bool closing;

    public static void Show(Toast t, Action onClick)
    {
        if (!t.Quiet && App.Settings.AlertSound) Chime.Play();
        new AlertWindow(t, onClick).Show();
    }

    AlertWindow(Toast t, Action onClick)
    {
        InitializeComponent();
        this.onClick = onClick;
        sticky = t.Sticky;

        Root.Background = BarWindow.PillBrush;
        Root.LayoutTransform = new ScaleTransform(BarWindow.Scale, BarWindow.Scale);
        var c = t.Color;
        Dot.Fill = new SolidColorBrush(c);
        Bar.Background = new LinearGradientBrush(Color.FromArgb(0x40, c.R, c.G, c.B), c, 0);
        Bar.Visibility = sticky ? Visibility.Collapsed : Visibility.Visible;
        if (t.Art is not null)
        {
            Art.ImageSource = t.Art;
            ArtBox.Visibility = Visibility.Visible;
            Dot.Visibility = Visibility.Collapsed;
        }
        Label.Text = t.Label;
        Value.Text = t.Headline;
        Unit.Text = t.Unit;
        Detail.Text = t.Detail;
        Detail.Visibility = t.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Side.Text = $"{t.SideTitle} {t.SideText}".Trim();
        Side.Visibility = Side.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Root.ToolTip = $"{t.Headline}{t.Unit}  ·  {DateTime.Now:HH:mm}";

        // Stack downward from under the bar, reusing the highest free slot.
        slot = Enumerable.Range(0, open.Count + 1).First(i => open.All(w => w.slot != i));
        open.Add(this);
        Left = -20000; // placed once its size is known

        SourceInitialized += (_, _) => Native.Passive(new WindowInteropHelper(this).Handle); // clicks don't take focus from your app
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) => Enter();
        MouseEnter += (_, _) => countdown.Pause(this);
        MouseLeave += (_, _) => countdown.Resume(this);
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        var pill = Root.TranslatePoint(new Point(0, Root.ActualHeight), this).Y; // top of window to bottom of pill, scaled
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = App.AlertTop + slot * (pill + Gap);
    }

    void Enter()
    {
        Motion.To(Root, OpacityProperty, 1, 300);
        Motion.To(Slide, TranslateTransform.YProperty, 0, 380);

        // Breathing status dot.
        Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(900))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        if (sticky) return;
        var drain = new DoubleAnimation(1, 0, Life);
        Storyboard.SetTarget(drain, Bar);
        Storyboard.SetTargetProperty(drain, new PropertyPath("RenderTransform.ScaleX"));
        countdown.Children.Add(drain);
        countdown.Completed += (_, _) => Dismiss();
        countdown.Begin(this, true);
    }

    void Dismiss()
    {
        if (closing) return;
        closing = true;
        open.Remove(this);
        Motion.To(Slide, TranslateTransform.YProperty, -10, 260);
        Motion.To(Root, OpacityProperty, 0, 260, done: Close);
    }

    void Clicked(object sender, MouseButtonEventArgs e)
    {
        onClick();
        Dismiss();
    }

    void CloseClicked(object sender, RoutedEventArgs e) => Dismiss();
}
