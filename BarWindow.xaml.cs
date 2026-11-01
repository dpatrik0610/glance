using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Pages;
using Glance.Services;
using Microsoft.Win32;

namespace Glance;

/// The always-on-top pill at the top center of the main screen. One view at a time: arrows or the wheel switch,
/// a click opens that view's page, the down arrow drops Glance under it. Views with nothing to show (no hometown,
/// no reminders, nothing playing) are skipped. Steps aside while a fullscreen app (game, video, slides) is in front.
public partial class BarWindow : Window
{
    record View(string Name, string Tab, string Icon);

    static readonly View[] Views =
    [
        new("System", "System", ""),
        new("Network", "Network", ""),
        new("Music", "Now Playing", ""),
        new("Weather", "Weather", ""),
        new("Exchange", "Exchange", ""),
        new("Reminder", "Notes", ""),
    ];

    static readonly TimeSpan WeatherAge = TimeSpan.FromMinutes(15), RatesAge = TimeSpan.FromHours(3);

    readonly Action<string?> drop;
    readonly DispatcherTimer tick, fullscreen, progress;
    bool away; // stepped aside for a fullscreen app
    int index;
    Snapshot? snap;
    Weather.Current? weather;
    DateTime weatherAt, ratesAt;
    string? weatherFor; // hometown + units the weather was fetched for
    (float Eur, float Usd)? rates;
    string? failed; // the view whose fetch failed, so it says so instead of "…"
    bool fetching;

    /// drop: opens Glance under the bar at that tab; null (the down arrow) toggles it.
    public BarWindow(Action<string?> drop)
    {
        InitializeComponent();
        this.drop = drop;
        index = Math.Max(0, Array.FindIndex(Views, v => v.Name == App.Settings.BarView));

        App.Sampled += OnSample;
        App.SettingsChanged += ApplyOpacity;
        ApplyOpacity();
        Media.Changed += OnMedia;
        SystemEvents.DisplaySettingsChanged += Moved;
        // Each half minute: relative times ("in 5 min") move on, and stale weather/rates refresh while shown.
        tick = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => { Render(); Fetch(); }, Dispatcher);
        // One cheap shell query a second (no hooks): is a fullscreen app in front?
        fullscreen = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => CheckFullscreen(), Dispatcher);
        fullscreen.Stop();
        // The song progress line: once a second, only while the Music view shows something playing.
        progress = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowProgress(), Dispatcher);
        progress.Stop();
        Closed += (_, _) =>
        {
            App.Sampled -= OnSample;
            App.SettingsChanged -= ApplyOpacity;
            Media.Changed -= OnMedia;
            fullscreen.Stop();
            progress.Stop();
            SystemEvents.DisplaySettingsChanged -= Moved;
            tick.Stop();
        };

        SourceInitialized += (_, _) => Native.Passive(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) => SlideIn();
        Show(0);
    }

    void OnSample(Snapshot s) => Dispatcher.BeginInvoke(() =>
    {
        snap = s;
        if (Views[index].Name is "System" or "Network") Render();
    });

    void OnMedia()
    {
        if (Views[index].Name == "Music") Render();
    }

    // Background only, like the main window: the text stays fully readable.
    void ApplyOpacity() =>
        Pill.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(App.Settings.BarOpacity, 0.1, 1) * 255), 0x14, 0x14, 0x17));

    /// Where the bar ends, for dropping Glance just below it.
    public double Edge => Top + Pill.Margin.Top + Pill.ActualHeight;

    /// The down arrow turns up while Glance hangs under the bar.
    public void Dropped(bool on) => Motion.To(Turn, RotateTransform.AngleProperty, on ? 180 : 0, 260);

    void SlideIn()
    {
        Motion.To(Pill, OpacityProperty, 1, 300);
        Motion.To(Slide, TranslateTransform.YProperty, 0, 380);
        fullscreen.Start();
    }

    void CheckFullscreen()
    {
        var hide = App.Settings.BarHideFullscreen && Native.FullscreenApp();
        if (hide == away) return;
        away = hide;
        if (hide)
        {
            // Hidden, not just transparent: a topmost window over a game can cost it its fullscreen fast path.
            Motion.To(Pill, OpacityProperty, 0, 180);
            Motion.To(Slide, TranslateTransform.YProperty, -Pill.ActualHeight - Pill.Margin.Top, 220, done: () => { if (away) Hide(); });
        }
        else
        {
            Show();
            SlideIn();
        }
    }

    // Top center of the main screen's work area (below a top taskbar, if any).
    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top;
    }

    void Moved(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Place);

    // ---- Switching ----

    void Prev(object sender, RoutedEventArgs e) => Show(-1);
    void Next(object sender, RoutedEventArgs e) => Show(+1);
    void Wheel(object sender, MouseWheelEventArgs e) => Show(e.Delta > 0 ? -1 : +1);

    void OpenTab(object sender, MouseButtonEventArgs e) => drop(Views[index].Tab);
    void Down(object sender, RoutedEventArgs e) => drop(null);

    /// Moves `step` views (0: stay), skipping views that have nothing to show.
    void Show(int step)
    {
        var dir = step == 0 ? 1 : Math.Sign(step);
        var i = (index + step + Views.Length) % Views.Length;
        for (var n = 0; n < Views.Length && Read(Views[i]) is null; n++) i = (i + dir + Views.Length) % Views.Length;
        if (step != 0 && i != index)
        {
            Motion.To(Body, OpacityProperty, 1, 220, from: 0);
            Motion.To(Shift, TranslateTransform.XProperty, 0, 260, from: 14 * dir);
        }
        index = i;
        if (App.Settings.BarView != Views[i].Name)
        {
            App.Settings.BarView = Views[i].Name;
            App.Settings.Save();
        }
        Render();
        Fetch();
    }

    // ---- Content ----

    void Render()
    {
        var v = Views[index];
        if (Read(v) is not { } segs) { Show(1); return; } // e.g. the last reminder was ticked off
        Glyph.Text = v.Icon;
        Body.ToolTip = $"Open {v.Tab}";
        ShowMusic(v.Name == "Music" ? Media.Current : null);
        Segs.Children.Clear();
        foreach (var (dim, bright) in segs)
        {
            if (Segs.Children.Count > 0) Segs.Children.Add(new Border { Width = 14 });
            if (dim.Length > 0)
                Segs.Children.Add(new TextBlock { Text = dim, FontSize = 11, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("Sub"), Margin = new(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            Segs.Children.Add(new TextBlock
            {
                Text = bright, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, // long song titles
            });
        }
    }

    // Music view extras: cover instead of the icon, prev/play/next, and the progress line.
    void ShowMusic(Player? p)
    {
        var on = p is not null;
        Controls.Visibility = Progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CoverBox.Visibility = p?.Cover is not null ? Visibility.Visible : Visibility.Collapsed;
        Glyph.Visibility = p?.Cover is null ? Visibility.Visible : Visibility.Collapsed;
        if (p is null) { progress.Stop(); return; }
        CoverArt.ImageSource = p.Cover;
        PlayPause.Content = p.Playing ? "\uE769" : "\uE768";
        PlayPause.ToolTip = p.Playing ? "Pause" : "Play";
        PlayPause.IsEnabled = p.CanToggle;
        PrevTrack.IsEnabled = p.CanPrev;
        NextTrack.IsEnabled = p.CanNext;
        Progress.Visibility = p.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        ShowProgress();
        if (p.Playing && p.Duration > TimeSpan.Zero) progress.Start(); else progress.Stop();
    }

    void ShowProgress()
    {
        if (Media.Current is not { Duration.Ticks: > 0 } p) return;
        // Glides to where the song will be a second from now, so the line moves smoothly instead of ticking.
        var ahead = p.Playing ? p.Position + TimeSpan.FromSeconds(1) : p.Position;
        Motion.To(Done, ScaleTransform.ScaleXProperty, Math.Clamp(ahead / p.Duration, 0, 1), p.Playing ? 1000 : 200);
    }

    void TrackPrev(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Prev); }
    void TrackToggle(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Toggle); }
    void TrackNext(object sender, RoutedEventArgs e) { if (Media.Current is { } p) Media.Do(p.Next); }

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// What a view shows as (dim label, bright value) pairs; null when it has nothing to show.
    (string, string)[]? Read(View v)
    {
        var s = App.Settings;
        switch (v.Name)
        {
            case "System":
                if (snap is null) return [("", "…")];
                var cpu = snap.CpuTemp is { } t ? $"{snap.CpuLoad:0}% · {t:0}°" : $"{snap.CpuLoad:0}%";
                List<(string, string)> sys = [("CPU", cpu)];
                if (snap.GpuLoad is { } g) sys.Add(("GPU", snap.GpuTemp is { } gt ? $"{g:0}% · {gt:0}°" : $"{g:0}%"));
                if (snap.RamUsed is { } used && snap.RamTotal is > 0 and var total) sys.Add(("RAM", $"{used / total * 100:0}%"));
                return [.. sys];
            case "Network":
                return snap is null ? [("", "…")] : [("↓", SystemPage.Rate(snap.NetDown)), ("↑", SystemPage.Rate(snap.NetUp))];
            case "Music":
                if (Media.Current is not { Title.Length: > 0 } p) return null;
                return [(p.Artist, p.Title)];
            case "Weather":
                if (s.Lat is null) return null; // no hometown set on the Weather page
                var place = s.City.Length > 0 ? s.City : "Weather";
                if (weather is not { } w) return [(place, failed == v.Name ? "can't reach Open-Meteo" : "…")];
                return [(place, $"{w.Temp:0}°"), ("", Weather.Describe(w.Code, w.IsDay == 1).Text)];
            case "Exchange":
                if (rates is not { } r) return [("EUR", failed == v.Name ? "can't reach the ECB rates" : "…")];
                return [("EUR", $"{r.Eur:0.00} Ft"), ("USD", $"{r.Usd:0.00} Ft")];
            case "Reminder":
                var now = DateTime.Now;
                var next = NoteStore.Notes.SelectMany(n => n.Items).Where(i => !i.Done && i.Due is not null).MinBy(i => i.Due);
                return next is null ? null : [(Due.Format(next.Due!.Value, now), next.Text.Length > 0 ? next.Text : "(no text)")];
        }
        return null;
    }

    // Weather and rates load only while their view is on screen, and only when stale.
    async void Fetch()
    {
        var v = Views[index].Name;
        if (fetching) return;
        var s = App.Settings;
        fetching = true;
        try
        {
            if (v == "Weather" && s.Lat is { } lat && s.Lon is { } lon
                && (DateTime.Now - weatherAt > WeatherAge || weatherFor != $"{lat},{lon},{s.Fahrenheit}"))
            {
                weather = (await Weather.Get(lat, lon, s.Fahrenheit))?.Current;
                weatherAt = DateTime.Now;
                weatherFor = $"{lat},{lon},{s.Fahrenheit}";
            }
            else if (v == "Exchange" && DateTime.Now - ratesAt > RatesAge)
            {
                var (_, huf) = await Rates.Get(7);
                rates = (huf["EUR"][^1], huf["USD"][^1]);
                ratesAt = DateTime.Now;
            }
            else return;
            if (failed == v) failed = null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            failed = v; // keeps showing the last good value if there is one; retried on the next tick
        }
        finally { fetching = false; }
        Render();
    }
}
