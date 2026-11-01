using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

public partial class NowPlayingPage : UserControl
{
    // Moves the progress bar between the player's own updates; only runs while the page is on screen and something plays.
    readonly DispatcherTimer tick;
    string sources = "";

    public NowPlayingPage()
    {
        InitializeComponent();
        tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Time(), Dispatcher);
        Media.Changed += Show;
        IsVisibleChanged += (_, _) => Show();
        Show();
    }

    void Show()
    {
        if (!IsVisible) { tick.Stop(); return; }
        var p = Media.Current;
        Card.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = p is null ? Visibility.Visible : Visibility.Collapsed;
        if (p is null) { tick.Stop(); return; }

        ShowSources(p);
        TitleText.Text = p.Title.Length > 0 ? p.Title : p.Source;
        ArtistText.Text = p.Artist;
        AlbumText.Text = p.Album;
        AlbumText.Visibility = p.Album.Length > 0 && p.Album != p.Title ? Visibility.Visible : Visibility.Collapsed; // singles: album = title
        Cover.ImageSource = p.Cover;
        PlayButton.Content = p.Playing ? "" : "";
        PlayButton.ToolTip = p.Playing ? "Pause" : "Play";
        PlayButton.IsEnabled = p.CanToggle;
        PrevButton.IsEnabled = p.CanPrev;
        NextButton.IsEnabled = p.CanNext;

        Timeline.Visibility = p.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        Seek.Maximum = p.Duration.TotalSeconds;
        Seek.IsEnabled = p.CanSeek; // shown, but read-only, when the player doesn't take seeks
        Seek.ToolTip = p.CanSeek ? null : $"{p.Source} doesn't allow seeking from outside";
        Time();
        if (p.Playing && p.Duration > TimeSpan.Zero) tick.Start(); else tick.Stop();
    }

    void Time()
    {
        if (Media.Current is not { } p) return;
        var at = p.Position;
        if (!Seek.IsMouseCaptureWithin) Seek.Value = at.TotalSeconds; // not while being dragged
        Elapsed.Text = Clock(Seek.IsMouseCaptureWithin ? TimeSpan.FromSeconds(Seek.Value) : at);
        Total.Text = Clock(p.Duration);
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // A chip per player (switches between them); rebuilt only when the set of players changes.
    void ShowSources(Player current)
    {
        var key = string.Join("|", Media.Players.Select(p => $"{p.GetHashCode()}{p.Playing}{p == current}"));
        if (key == sources) return;
        sources = key;
        Sources.Children.Clear();
        foreach (var p in Media.Players)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "player",
                Content = p.Playing ? $"{p.Source}  ▶" : p.Source,
                ToolTip = p.Title.Length > 0 ? p.Title : null,
                IsChecked = p == current,
                Margin = new(0, 0, 6, 0),
            };
            chip.Checked += (_, _) => { if (Media.Current != p) Media.Pick(p); };
            Sources.Children.Add(chip);
        }
    }

    void Seeked(object sender, MouseButtonEventArgs e)
    {
        if (Media.Current is { CanSeek: true } p) Media.Do(() => p.Seek(TimeSpan.FromSeconds(Seek.Value)));
    }

    void Toggle(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Toggle); }
    void Prev(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Prev); }
    void Next(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Next); }
}
