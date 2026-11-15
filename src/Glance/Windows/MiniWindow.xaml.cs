using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Pages;
using Glance.Services;

namespace Glance;

/// The bar's panel: the shown view in more detail, as wide as the pill (at least 300), hung under it. Music is the player
/// (seek, controls, volume); every other view is a header like the player's (its icon, a title, a line under it) and its
/// details. The header opens that view's page. It follows the bar to another view. Esc, a click on the bar's view or
/// arrow again, or a click anywhere else closes it.
public partial class MiniWindow : Window
{
    const double Shadow = 32; // the card's left + right margin
    const double MinCard = 300;
    static readonly Color Up = Color.FromRgb(0x4C, 0xC3, 0x7A), Down = Color.FromRgb(0xFF, 0x4D, 0x5E);
    readonly BarWindow bar;
    readonly Action<string?> open;
    readonly DispatcherTimer tick; // while the player shows: song position and volume
    SpeedGauge? gauge; // kept across renders: it animates on its own
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
        Media.Changed += OnMedia;
        SpeedRun.Changed += OnSpeed;
        bar.SizeChanged += Fit; // the pill changes width with what it shows
        Closed += (_, _) => { tick.Stop(); Media.Changed -= OnMedia; SpeedRun.Changed -= OnSpeed; bar.SizeChanged -= Fit; };
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

    void OnMedia()
    {
        if (bar.ViewName == "Music") Render();
    }

    void OnSpeed()
    {
        if (bar.ViewName == "Network") Render();
    }

    void Fit(object? sender, EventArgs e)
    {
        Width = Math.Max(bar.PillWidth, MinCard) + Shadow;
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

    /// The shown view, again: the bar calls this with each update of its own.
    public void Render()
    {
        var view = bar.ViewName;
        var player = view == "Music" && Media.Current is not null;
        Player.Visibility = player ? Visibility.Visible : Visibility.Collapsed;
        Info.Visibility = player ? Visibility.Collapsed : Visibility.Visible;
        if (player) { ShowPlayer(Media.Current!); return; }
        tick.Stop();
        Info.Children.Clear();
        switch (view)
        {
            case "Music": Add(Header(bar.ViewIcon, "Nothing playing", "Play something to control it here", "Now Playing")); break;
            case "System": ShowSystem(); break;
            case "Network": ShowNetwork(); break;
            case "Weather": ShowWeather(); break;
            case "Exchange": ShowExchange(); break;
            case "Reminder": ShowReminders(); break;
        }
    }

    void Add(UIElement e) => Info.Children.Add(e);

    // ---- Views ----

    void ShowSystem()
    {
        if (bar.Snap is not { } s) { Add(Header(bar.ViewIcon, "System", "Reading the sensors…", "System")); return; }
        Add(Header(bar.ViewIcon, s.CpuName.Length > 0 ? s.CpuName : "CPU", s.GpuName, "System"));
        if (s.CpuLoad is { } cpu) Add(Meter("CPU", cpu / 100, s.CpuTemp is { } t ? $"{cpu:0}% · {t:0}°" : $"{cpu:0}%"));
        if (s.GpuLoad is { } gpu) Add(Meter("GPU", gpu / 100, s.GpuTemp is { } t ? $"{gpu:0}% · {t:0}°" : $"{gpu:0}%"));
        if (s.RamUsed is { } ram && s.RamTotal is > 0 and var total) Add(Meter("RAM", ram / total, $"{ram:0.0} / {total:0} GB"));
        if (s.VramUsed is { } vram && s.VramTotal is > 0 and var vtotal) Add(Meter("VRAM", vram / vtotal, $"{vram / 1024:0.0} / {vtotal / 1024:0} GB"));
        if (s.Procs.MaxBy(p => p.Cpu) is { Cpu: > 0 } top)
            Add(Line($"Busiest: {top.Name} · {top.Cpu:0}% CPU · {top.MemMb / 1024:0.0} GB", 10));
    }

    void ShowNetwork()
    {
        if (bar.Snap is not { } s) { Add(Header(bar.ViewIcon, "Network", "Measuring…", "Network")); return; }
        Add(Header(bar.ViewIcon, $"↓ {SystemPage.Rate(s.NetDown)}    ↑ {SystemPage.Rate(s.NetUp)}", "The last minute", "Network"));
        Add(Graph("Download", bar.Downs, Color.FromRgb(0x5A, 0xA9, 0xFF), "rate"));
        Add(Graph("Upload", bar.Ups, Color.FromRgb(0xB0, 0x7C, 0xFF), "rate"));
        // The speed test: the page's dial, smaller; start it from either
        gauge ??= new SpeedGauge { Height = 170, Margin = new(0, 12, 0, 0) };
        Add(gauge);
        var stats = new UniformGrid { Columns = 3 };
        stats.Children.Add(Stat("Ping", SpeedRun.Ping is { } p ? $"{p:0} ms" : "—"));
        stats.Children.Add(Stat("Download", SpeedRun.Down is { } d ? $"{SpeedRun.Mbps(d)} Mbps" : "—"));
        stats.Children.Add(Stat("Upload", SpeedRun.Up is { } u ? $"{SpeedRun.Mbps(u)} Mbps" : "—"));
        Add(stats);
        var note = Line(SpeedRun.Note, 6);
        note.ToolTip = SpeedRun.Note;
        Add(note);
    }

    void ShowWeather()
    {
        var set = App.Settings;
        if (set.Lat is null) { Add(Header(bar.ViewIcon, "No hometown set", "Pick your city on the Weather page", "Weather")); return; }
        var city = set.City.Length > 0 ? set.City : "Weather";
        if (bar.Forecast is not { } f) { Add(Header(bar.ViewIcon, city, bar.Failed ? "Can't reach Open-Meteo" : "Loading…", "Weather")); return; }
        var c = f.Current;
        var (glyph, text) = Weather.Describe(c.Code, c.IsDay == 1);
        Add(Header(glyph, $"{c.Temp:0}° · {text}", city, "Weather", iconFont: false));
        var stats = new UniformGrid { Columns = 3, Margin = new(0, 12, 0, 0) };
        stats.Children.Add(Stat("Feels like", $"{c.Feels:0}°"));
        stats.Children.Add(Stat("Wind", $"{c.Wind:0} {(set.Fahrenheit ? "mph" : "km/h")}"));
        stats.Children.Add(Stat("Humidity", $"{c.Humidity}%"));
        Add(stats);
        if (f.Daily.Max.Length > 0)
            Add(Line($"Today {f.Daily.Max[0]:0}° / {f.Daily.Min[0]:0}°" + (f.Daily.Rain[0] is { } rain ? $" · rain {rain}%" : ""), 10));
    }

    void ShowExchange()
    {
        if (bar.Week is not var (eur, usd)) { Add(Header(bar.ViewIcon, "Exchange rates", bar.Failed ? "Can't reach the ECB rates" : "Loading…", "Exchange")); return; }
        Add(Header(bar.ViewIcon, $"1 EUR = {eur[^1]:0.00} Ft", "ECB reference rates · the last week", "Exchange"));
        Add(Rate("EUR", eur));
        Add(Rate("USD", usd));
    }

    void ShowReminders()
    {
        var now = DateTime.Now;
        var due = (from n in NoteStore.Everything from i in n.Items where !i.Done && i.Due is not null orderby i.Due select (n, i)).ToList();
        if (due.Count == 0) { Add(Header(bar.ViewIcon, "No reminders", "Due checklist items show up here", "Notes")); return; }
        var late = due.Count(d => d.i.Due < now);
        Add(Header(bar.ViewIcon, "Coming up", $"{due.Count} due" + (late > 0 ? $" · {late} overdue" : ""), "Notes"));
        foreach (var (note, item) in due.Take(4)) Add(Reminder(note, item, now));
        if (due.Count > 4) Add(Line($"+{due.Count - 4} more in Notes and Stickies", 6));
    }

    // ---- Pieces ----

    Brush R(string key) => (Brush)FindResource(key);

    /// Like the player's song line: a tile with the view's glyph, a title and a line under it. Opens the view's page.
    FrameworkElement Header(string glyph, string title, string sub, string tab, bool iconFont = true)
    {
        var tile = new Grid { Width = 46, Height = 46, Margin = new(0, 0, 12, 0) };
        tile.Children.Add(new Border { CornerRadius = new(8), Background = R("Track") });
        var mark = new TextBlock { Text = glyph, FontSize = iconFont ? 18 : 22, Foreground = R("Accent"), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        if (iconFont) mark.FontFamily = (FontFamily)FindResource("Icons");
        tile.Children.Add(mark);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub.Length > 0)
            text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = R("Sub"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 2, 0, 0) });
        var head = new DockPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = $"Open {tab}" };
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        head.Children.Add(text);
        head.MouseLeftButtonUp += (_, _) => { Away(); open(tab); };
        return head;
    }

    TextBlock Line(string text, double top) => new()
    {
        Text = text, FontSize = 11.5, Foreground = R("Sub"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, top, 0, 0),
    };

    // Label, a bar filled to `fraction`, the value.
    FrameworkElement Meter(string label, double fraction, string value)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var row = new DockPanel { Margin = new(0, 9, 0, 0) };
        var name = new TextBlock { Text = label, FontSize = 11.5, Foreground = R("Sub"), Width = 40, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.SemiBold, MinWidth = 92, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(val, Dock.Right);
        var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 10, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new(fraction, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1 - fraction, GridUnitType.Star) });
        var back = new Border { CornerRadius = new(2.5), Background = R("Track") };
        Grid.SetColumnSpan(back, 2);
        track.Children.Add(back);
        track.Children.Add(new Border { CornerRadius = new(2.5), Background = R("Accent") });
        row.Children.Add(name);
        row.Children.Add(val);
        row.Children.Add(track);
        return row;
    }

    FrameworkElement Stat(string label, string value)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = R("Sub") });
        s.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 2, 0, 0) });
        return s;
    }

    FrameworkElement Graph(string label, IReadOnlyList<float> series, Color color, string format)
    {
        var s = new StackPanel { Margin = new(0, 10, 0, 0) };
        s.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = R("Sub") });
        s.Children.Add(new Spark { Series = series.ToArray(), Color = color, Max = 0, Format = format, Height = 26, Margin = new(0, 4, 0, 0) });
        return s;
    }

    // A currency: today's rate, the change since the day before (green up, red down) and the week.
    FrameworkElement Rate(string code, float[] days)
    {
        var row = new DockPanel { Margin = new(0, 10, 0, 0) };
        var name = new TextBlock { Text = code, FontSize = 11.5, Foreground = R("Sub"), Width = 40, VerticalAlignment = VerticalAlignment.Center };
        var value = new StackPanel { Width = 112, VerticalAlignment = VerticalAlignment.Center };
        value.Children.Add(new TextBlock { Text = $"{days[^1]:0.00} Ft", FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (days.Length > 1)
        {
            var change = days[^1] - days[^2];
            value.Children.Add(new TextBlock
            {
                Text = change == 0 ? "no change" : $"{(change > 0 ? "▲" : "▼")} {Math.Abs(change):0.00}", FontSize = 11,
                Foreground = change == 0 ? R("Sub") : new SolidColorBrush(change > 0 ? Up : Down),
            });
        }
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(value, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(value);
        row.Children.Add(new Spark { Series = days, Color = Color.FromRgb(0x5A, 0xA9, 0xFF), Max = 0, Format = "{0:0.00} Ft", Step = TimeSpan.FromDays(1), Height = 26 });
        return row;
    }

    // A due item: tick it off here; when it's due on the right (red once overdue), its note under it.
    FrameworkElement Reminder(Note note, TodoItem item, DateTime now)
    {
        var row = new DockPanel { Margin = new(0, 10, 0, 0) };
        var check = new CheckBox { Style = (Style)FindResource("Check"), DataContext = item, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 10, 0),
            ToolTip = "Done" };
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(TodoItem.Done)));
        check.Click += (_, _) => bar.Refresh(); // the bar and this list move on to the next one
        var when = new TextBlock
        {
            Text = Due.Format(item.Due!.Value, now), FontSize = 11.5, Margin = new(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Foreground = item.Due < now ? R("Red") : R("Sub"),
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Text.Length > 0 ? item.Text : "(no text)", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = (note is Sticky ? "Sticky · " : "Note · ") + note.DisplayTitle, FontSize = 11, Foreground = R("Sub"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 1, 0, 0) });
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(when, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(when);
        row.Children.Add(text);
        return row;
    }

    // ---- Music: the player ----

    void ShowPlayer(Player p)
    {
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
