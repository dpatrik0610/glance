using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance;

/// The down arrow's player: as wide as the pill, hung under it, with seek, controls and volume.
/// A click on the song opens Now Playing. Esc, the arrow again, or a click anywhere else closes it.
public partial class MiniWindow : Window
{
    const double Shadow = 32; // the card's left + right margin
    readonly BarWindow bar;
    readonly Action<string?> open;
    readonly DispatcherTimer tick; // while open: song position and volume
    bool closing;

    public MiniWindow(BarWindow bar, Action<string?> open)
    {
        InitializeComponent();
        this.bar = bar;
        this.open = open;
        // Readable over anything even when the bar is set see-through.
        var c = ((SolidColorBrush)BarWindow.PillBrush).Color;
        Card.Background = new SolidColorBrush(Color.FromArgb(Math.Max(c.A, (byte)0xE0), c.R, c.G, c.B));

        tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        Media.Changed += Render;
        bar.SizeChanged += Fit; // the pill changes width with what it shows
        Closed += (_, _) => { tick.Stop(); Media.Changed -= Render; bar.SizeChanged -= Fit; };
        Deactivated += (_, _) => Away();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Away(); };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Motion.To(Card, OpacityProperty, 1, 220);
            Motion.To(Slide, TranslateTransform.YProperty, 0, 280);
        };
        Fit(this, EventArgs.Empty);
        Render();
    }

    void Fit(object? sender, EventArgs e)
    {
        Width = bar.PillWidth + Shadow;
        Place();
    }

    void Place()
    {
        Left = bar.Center - ActualWidth / 2;
        Top = bar.Edge - 4;
    }

    public void Away()
    {
        if (closing) return;
        closing = true;
        Motion.To(Card, OpacityProperty, 0, 150);
        Motion.To(Slide, TranslateTransform.YProperty, -8, 180, done: Close);
    }

    void Render()
    {
        var p = Media.Current;
        Player.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = p is null ? Visibility.Visible : Visibility.Collapsed;
        if (p is null) { tick.Stop(); return; }
        Song.Text = p.Title.Length > 0 ? p.Title : p.Source;
        Artist.Text = p.Artist.Length > 0 ? $"{p.Artist} · {p.Source}" : p.Source;
        Cover.ImageSource = p.Cover;
        PlayButton.Content = p.Playing ? "" : "";
        PlayButton.ToolTip = p.Playing ? "Pause" : "Play";
        PlayButton.IsEnabled = p.CanToggle;
        PrevButton.IsEnabled = p.CanPrev;
        NextButton.IsEnabled = p.CanNext;
        Seek.Visibility = p.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        Seek.Maximum = p.Duration.TotalSeconds;
        Seek.IsEnabled = p.CanSeek;
        Seek.ToolTip = p.CanSeek ? null : $"{p.Source} doesn't allow seeking from outside";
        if (!Seek.IsMouseCaptureWithin) Seek.Value = p.Position.TotalSeconds;
        Volume.Show(p);
        if (!tick.IsEnabled) tick.Start();
    }

    void Seeked(object sender, MouseButtonEventArgs e)
    {
        if (Media.Current is { CanSeek: true } p) Media.Do(() => p.Seek(TimeSpan.FromSeconds(Seek.Value)));
    }

    void Toggle(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Toggle); }
    void Prev(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Prev); }
    void Next(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Next); }

    void OpenPlayer(object sender, MouseButtonEventArgs e)
    {
        Away();
        open("Now Playing");
    }
}
