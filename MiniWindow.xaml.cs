using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance;

/// The down arrow's panel: 300px under the bar with the player (seek, controls, volume) and every bar view at once.
/// A click on a row opens that page under the bar. Esc, the arrow again, or a click anywhere else closes it.
public partial class MiniWindow : Window
{
    readonly BarWindow bar;
    readonly Action<string?> open;
    readonly DispatcherTimer tick; // while open: live numbers, song position, volume
    readonly Dictionary<string, WrapPanel> rows = [];
    bool closing;

    public MiniWindow(BarWindow bar, Action<string?> open)
    {
        InitializeComponent();
        this.bar = bar;
        this.open = open;
        // Readable over anything even when the bar is set see-through.
        var c = ((SolidColorBrush)BarWindow.PillBrush).Color;
        Card.Background = new SolidColorBrush(Color.FromArgb(Math.Max(c.A, (byte)0xE0), c.R, c.G, c.B));

        foreach (var v in BarWindow.Views.Where(v => v.Name != "Music")) Rows.Children.Add(Row(v));

        tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Media.Changed += Refresh;
        Closed += (_, _) => { tick.Stop(); Media.Changed -= Refresh; };
        Deactivated += (_, _) => Away();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Away(); };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Motion.To(Card, OpacityProperty, 1, 220);
            Motion.To(Slide, TranslateTransform.YProperty, 0, 280);
        };
        Refresh();
        FetchAll();
    }

    async void FetchAll()
    {
        await bar.Fetch("Weather");
        await bar.Fetch("Exchange");
        Refresh();
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

    // ---- Rows: one per bar view, text refreshed in place (so hover survives the updates) ----

    Border Row(BarWindow.View v)
    {
        var segs = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        rows[v.Name] = segs;
        var icon = new TextBlock
        {
            Text = v.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, Foreground = (Brush)FindResource("Accent"),
            Width = 26, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        var row = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = $"Open {v.Tab}", Child = new DockPanel { Children = { icon, segs } },
        };
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("CardBg");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => Go(v.Tab);
        return row;
    }

    void Refresh()
    {
        foreach (var v in BarWindow.Views.Where(v => v.Name != "Music"))
        {
            var segs = rows[v.Name];
            var read = bar.Read(v);
            ((FrameworkElement)segs.Parent).Parent.SetValue(VisibilityProperty, read is null ? Visibility.Collapsed : Visibility.Visible);
            segs.Children.Clear();
            foreach (var (dim, bright) in read ?? [])
            {
                var seg = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 12, 1) };
                if (dim.Length > 0) seg.Children.Add(new TextBlock { Text = dim, FontSize = 11.5, Foreground = (Brush)FindResource("Sub"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis });
                seg.Children.Add(new TextBlock { Text = bright, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis });
                segs.Children.Add(seg);
            }
        }
        ShowPlayer();
        if (!tick.IsEnabled) tick.Start();
    }

    // ---- Player ----

    void ShowPlayer()
    {
        var p = Media.Current;
        MediaCard.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        if (p is null) return;
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
    }

    void Seeked(object sender, MouseButtonEventArgs e)
    {
        if (Media.Current is { CanSeek: true } p) Media.Do(() => p.Seek(TimeSpan.FromSeconds(Seek.Value)));
    }

    void Toggle(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Toggle); }
    void Prev(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Prev); }
    void Next(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Next); }

    void OpenPlayer(object sender, MouseButtonEventArgs e) => Go("Now Playing");
    void OpenGlance(object sender, RoutedEventArgs e) => Go(null);

    void Go(string? tab)
    {
        Away();
        open(tab);
    }
}
