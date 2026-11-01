using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Glance.Services;

namespace Glance;

/// What a toast shows. Headline with a Unit = a metric (big number); without = a sentence (wrapped).
/// Sticky toasts stay until clicked or closed; others drain away (paused while hovered).
public record Toast(string Label, string Headline, string Unit, string SideTitle, string SideText, Color Color, bool Sticky);

/// Custom toast: slides in bottom-right, glows in its color.
public partial class AlertWindow : Window
{
    static readonly List<AlertWindow> open = [];
    static readonly TimeSpan Life = TimeSpan.FromSeconds(8);

    readonly Action onClick;
    readonly int slot;
    readonly Storyboard countdown = new();
    bool closing;

    readonly bool sticky;

    public static void Show(Toast t, Action onClick) => new AlertWindow(t, onClick).Show();

    AlertWindow(Toast t, Action onClick)
    {
        InitializeComponent();
        this.onClick = onClick;
        sticky = t.Sticky;

        var c = t.Color;
        Glow.Color = c;
        EdgeStop.Color = Color.FromArgb(0xCC, c.R, c.G, c.B);
        WashStop.Color = Color.FromArgb(0x30, c.R, c.G, c.B);
        Dot.Fill = new SolidColorBrush(c);
        Bar.Background = new LinearGradientBrush(Color.FromArgb(0x40, c.R, c.G, c.B), c, 0);
        Label.Text = Spaced(t.Label.ToUpperInvariant());
        Value.Text = t.Headline;
        Unit.Text = t.Unit;
        if (t.Unit.Length == 0) // sentence, not a number
        {
            Value.FontSize = 20;
            Value.FontWeight = FontWeights.Normal;
            Value.TextWrapping = TextWrapping.Wrap;
            Value.TextTrimming = TextTrimming.CharacterEllipsis;
            Value.MaxHeight = 52;
            Value.MaxWidth = 270; // the row is a horizontal StackPanel, so wrapping needs a width
        }
        SideTitle.Text = Spaced(t.SideTitle.ToUpperInvariant());
        SideText.Text = t.SideText;
        ((FrameworkElement)Bar.Parent).Visibility = sticky ? Visibility.Collapsed : Visibility.Visible; // no countdown
        Time.Text = DateTime.Now.ToString("HH:mm");

        // Stack upward from the bottom-right corner, reusing the lowest free slot.
        slot = Enumerable.Range(0, open.Count + 1).First(i => open.All(w => w.slot != i));
        open.Add(this);
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width;
        Top = area.Bottom - Height * (slot + 1) + 16 * slot;

        Loaded += (_, _) => Enter();
        MouseEnter += (_, _) => countdown.Pause(this);
        MouseLeave += (_, _) => countdown.Resume(this);
    }

    void Enter()
    {
        Motion.To(Root, OpacityProperty, 1, 350);
        Motion.To(Slide, TranslateTransform.XProperty, 0, 450);

        // Breathing status dot.
        Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(900))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        var drain = new DoubleAnimation(1, 0, Life);
        Storyboard.SetTarget(drain, Bar);
        Storyboard.SetTargetProperty(drain, new PropertyPath("RenderTransform.ScaleX"));
        if (sticky) return;
        countdown.Children.Add(drain);
        countdown.Completed += (_, _) => Dismiss();
        countdown.Begin(this, true);
    }

    void Dismiss()
    {
        if (closing) return;
        closing = true;
        open.Remove(this);
        Motion.To(Slide, TranslateTransform.XProperty, 40, 300);
        Motion.To(Root, OpacityProperty, 0, 300, done: Close);
    }

    void Clicked(object sender, MouseButtonEventArgs e)
    {
        onClick();
        Dismiss();
    }

    void CloseClicked(object sender, RoutedEventArgs e) => Dismiss();

    // WPF has no letter-spacing; hair spaces give the wide, technical look.
    static string Spaced(string s) => string.Join("\u200A", s.ToCharArray());
}
