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
/// the Glance icon opens Glance on that view's page, the down arrow drops the player under it. Over a maximized
/// window (browser tabs at the top) it shrinks to a thin line until hovered. Views with nothing to show (no hometown,
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
    MiniWindow? mini; // the panel the down arrow opens
    bool mainDropped;
    readonly DispatcherTimer tick, fullscreen, progress;
    bool away; // stepped aside for a fullscreen app
    bool slim; // shrunk to the line over a maximized window
    readonly DispatcherTimer peek; // hover delay before the line opens up
    int index;
    Snapshot? snap;
    Weather.Current? weather;
    DateTime weatherAt, ratesAt;
    string? weatherFor; // hometown + units the weather was fetched for
    (float Eur, float Usd)? rates;
    string? failed; // the view whose fetch failed, so it says so instead of "…"
    bool fetching;

    /// drop: opens Glance under the bar at that tab (null: the last one).
    public BarWindow(Action<string?> drop)
    {
        InitializeComponent();
        this.drop = drop;
        index = Math.Max(0, Array.FindIndex(Views, v => v.Name == App.Settings.BarView));

        App.Sampled += OnSample;
        App.SettingsChanged += ApplyLook;
        ApplyLook();
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
        peek = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => { peek!.Stop(); Slim(false); }, Dispatcher);
        peek.Stop();
        Closed += (_, _) =>
        {
            App.Sampled -= OnSample;
            App.SettingsChanged -= ApplyLook;
            mini?.Close();
            Media.Changed -= OnMedia;
            fullscreen.Stop();
            progress.Stop();
            peek.Stop();
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

    /// The pill's background (opacity applies to it only, so text stays readable) and size; alerts and the panel share them.
    public static Brush PillBrush => new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(App.Settings.BarOpacity, 0.1, 1) * 255), 0x14, 0x14, 0x17));
    public static double Scale => App.Settings.BarSize switch { "Medium" => 1.2, "Large" => 1.4, _ => 1 };

    void ApplyLook()
    {
        Pill.Background = PillBrush;
        SliverLine.Background = PillBrush;
        Pill.LayoutTransform = new ScaleTransform(Scale, Scale);
    }

    /// Where the pill ends on screen, for things hung below it.
    public double Edge => slim ? Top + Sliver.Height : Top + Pill.TranslatePoint(new Point(0, Pill.ActualHeight), this).Y;
    public double Center => Left + ActualWidth / 2;
    public double PillWidth => Pill.ActualWidth * Scale;

    /// The down arrow turns up while something hangs from the bar (the panel, or Glance itself).
    public void Dropped(bool on)
    {
        mainDropped = on;
        Turned();
    }

    void Turned() => Motion.To(Turn, RotateTransform.AngleProperty, mainDropped || mini is not null ? 180 : 0, 260);

    void SlideIn()
    {
        Motion.To(Pill, OpacityProperty, 1, 300);
        Motion.To(Slide, TranslateTransform.YProperty, 0, 380);
        fullscreen.Start();
    }

    void CheckFullscreen()
    {
        var hide = App.Settings.BarHideFullscreen && Native.FullscreenApp();
        if (hide == away) { CheckSlim(); return; }
        away = hide;
        if (hide)
        {
            // Hidden, not just transparent: a topmost window over a game can cost it its fullscreen fast path.
            mini?.Away();
            Motion.To(Pill, OpacityProperty, 0, 180);
            Motion.To(Slide, TranslateTransform.YProperty, -Pill.ActualHeight - Pill.Margin.Top, 220, done: () => { if (away) Hide(); });
        }
        else
        {
            Show();
            SlideIn();
        }
    }

    // Once a second: shrink over a maximized window once the mouse is off the bar and nothing hangs from it; open up
    // again when that window is gone (or Glance itself comes to the front).
    void CheckSlim()
    {
        if (away) return;
        var covered = App.Settings.BarSlim && Native.MaximizedUnder(new WindowInteropHelper(this).Handle);
        // While open over such a window, the pill's see-through margins hold the mouse too, so it doesn't flicker shut.
        Background = covered && !slim ? Hold : Brushes.Transparent;
        if (!covered) { if (slim) Slim(false); }
        else if (!slim && !IsMouseOver && mini is null && !mainDropped) Slim(true);
    }

    static readonly Brush Hold = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // invisible, but not click-through

    void Slim(bool on)
    {
        if (on == slim) return;
        slim = on;
        if (on)
        {
            Motion.To(Pill, OpacityProperty, 0, 160);
            Motion.To(Slide, TranslateTransform.YProperty, -Pill.ActualHeight - Pill.Margin.Top, 200, done: () =>
            {
                if (!slim) return;
                Pill.Visibility = Visibility.Collapsed;
                Sliver.Visibility = Visibility.Visible;
                Motion.To(SliverLine, OpacityProperty, 1, 200, from: 0);
            });
        }
        else
        {
            Background = Hold; // hovered open: the margins count as the bar until the next check
            Sliver.Visibility = Visibility.Collapsed;
            Pill.Visibility = Visibility.Visible;
            Motion.To(Pill, OpacityProperty, 1, 220);
            Motion.To(Slide, TranslateTransform.YProperty, 0, 260);
        }
    }

    void SliverEnter(object sender, MouseEventArgs e) => peek.Start();
    void SliverLeave(object sender, MouseEventArgs e) => peek.Stop();

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

    void OpenGlance(object sender, RoutedEventArgs e) => drop(Views[index].Tab);

    // Pick → hex on the clipboard, an alert in that color; clicking the alert opens the Color page on it.
    async void PickColor(object sender, RoutedEventArgs e)
    {
        if (await Eyedropper.Pick() is not { } c) return;
        var hex = ColorMath.Hex(c);
        var copied = ClipboardHistory.Copy(hex);
        ColorPage.Remember(c);
        AlertWindow.Show(new Toast(copied ? "Color · copied" : "Color · clipboard busy", hex, "", "RGB", $"{c.R} {c.G} {c.B}", c, false, Quiet: true), () =>
        {
            drop("Color");
            ((ColorPage)((MainWindow)Application.Current.MainWindow).ShowTab("Color")).Use(c);
        });
    }
    // The down arrow: the player under the pill; again, or a click elsewhere, closes it.
    void Down(object sender, RoutedEventArgs e)
    {
        if (mini is not null) { mini.Away(); return; }
        mini = new MiniWindow(this, drop) { Owner = this }; // owned: stays above the bar's z-order, out of Alt+Tab
        mini.Closed += (_, _) => { mini = null; Turned(); };
        mini.Show();
        mini.Activate();
        Turned();
    }

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

    string? shown; // the text Segs holds now

    void Render()
    {
        var v = Views[index];
        if (Read(v) is not { } segs) { Show(1); return; } // e.g. the last reminder was ticked off
        Glyph.Text = v.Icon;
        ShowMusic(v.Name == "Music" ? Media.Current : null);
        // Samples mostly repeat the last numbers: only rebuild the text when it changed.
        var text = string.Join('\n', segs.Select(s => s.Item1 + '\t' + s.Item2));
        if (text == shown) return;
        shown = text;
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
                var next = NoteStore.Everything.SelectMany(n => n.Items).Where(i => !i.Done && i.Due is not null).MinBy(i => i.Due);
                return next is null ? null : [(Due.Format(next.Due!.Value, now), next.Text.Length > 0 ? next.Text : "(no text)")];
        }
        return null;
    }

    void Fetch() => _ = Fetch(Views[index].Name);

    // Weather and rates load only while their view is on screen, and only when stale.
    async Task Fetch(string v)
    {
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
