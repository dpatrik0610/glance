using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// Timers, a stopwatch and Pomodoro. The state and the alerts live in Timers (they run with the page closed); this draws them.
public partial class TimerPage : UserControl
{
    readonly TimerData d = Timers.Data;
    readonly List<Action<DateTime>> updates = [];
    // Tenths on the stopwatch: redraws 10× a second, only while the page is on screen.
    readonly DispatcherTimer frame = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };

    public TimerPage()
    {
        InitializeComponent();
        frame.Tick += (_, _) => Update();
        IsVisibleChanged += (_, _) => { if (IsVisible) { frame.Start(); Update(); } else frame.Stop(); };
        foreach (var t in d.Timers) TimerList.Children.Add(TimerCard(t));
        WorkBox.Text = d.Work.ToString(CultureInfo.CurrentCulture);
        ShortBox.Text = d.ShortBreak.ToString(CultureInfo.CurrentCulture);
        LongBox.Text = d.LongBreak.ToString(CultureInfo.CurrentCulture);
        EveryBox.Text = d.LongEvery.ToString(CultureInfo.CurrentCulture);
        DrawLaps();
        Update();
    }

    void Update()
    {
        var now = DateTime.Now;
        foreach (var u in updates) u(now);
        NoTimers.Visibility = d.Timers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        WatchText.Text = Timers.Watch(Timers.Elapsed(now));
        WatchButton.Content = d.WatchStarted is null ? (d.WatchSeconds > 0 ? "Resume" : "Start") : "Stop";
        LapButton.IsEnabled = d.WatchStarted is not null;

        var on = d.Phase != "";
        PomoPhase.Text = d.Phase switch { "work" => "Work", "short" => "Short break", "long" => "Long break", _ => "Ready" };
        var left = on ? Timers.PhaseRemaining(now) : d.Work * 60;
        PomoText.Text = Timers.Clock(left);
        PomoScale.ScaleX = on ? left / Timers.PhaseLength(d.Phase) : 0;
        PomoFill.Background = (Brush)FindResource(d.Phase == "work" || !on ? "Accent" : "Green");
        PomoButton.Content = !on ? "Start" : d.PhaseEnds is null ? "Resume" : "Pause";
        PomoRounds.Text = on ? $"{d.Done} round{(d.Done == 1 ? "" : "s")} done" : "";
    }

    // ---- timers ----

    void AddTimer(object sender, RoutedEventArgs e)
    {
        var t = new TimerItem { Name = $"Timer {d.Timers.Count + 1}" };
        d.Timers.Add(t);
        Timers.SaveSoon();
        TimerList.Children.Add(TimerCard(t));
        Update();
    }

    Border TimerCard(TimerItem t)
    {
        TextBox Field(string text, string hint, double size, FontWeight weight) => new()
        {
            Text = text, Tag = hint, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), FontSize = size,
            FontWeight = weight, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var name = Field(t.Name, "Name", 15, FontWeights.SemiBold);
        name.TextChanged += (_, _) => { t.Name = name.Text; Timers.SaveSoon(); };
        var clock = new TextBlock { FontSize = 38, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 4, 0, 4) };
        var fill = new Border { Background = (Brush)FindResource("Accent"), CornerRadius = new(2), RenderTransform = new ScaleTransform(1, 1), RenderTransformOrigin = new(0, 0.5) };
        var bar = new Grid { Height = 4, Margin = new(0, 0, 0, 10), Children = { new Border { Background = (Brush)FindResource("Track"), CornerRadius = new(2) }, fill } };
        // Length: "25" = 25 min, "1:30" = 90 s. A new length resets the timer.
        var length = Field(Timers.Clock(t.Seconds), "Length", 12.5, FontWeights.Normal);
        length.Foreground = (Brush)FindResource("Sub");
        length.ToolTip = "Length: 25 = 25 min, 1:30 = 90 s, 1:00:00 = an hour";
        void SetLength()
        {
            if (Timers.ParseDuration(length.Text) is { } s && s != t.Seconds)
            {
                t.Seconds = s;
                Timers.Reset(t);
            }
            length.Text = Timers.Clock(t.Seconds);
        }
        length.LostFocus += (_, _) => SetLength();
        length.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SetLength(); Keyboard.ClearFocus(); } };

        Button Icon(string glyph, string tip, Action click)
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Content = glyph, FontFamily = (FontFamily)FindResource("Icons"), ToolTip = tip, Margin = new(2, 0, 2, 0) };
            b.Click += (_, _) => { click(); Update(); };
            return b;
        }
        var play = Icon("", "Start", () => Timers.StartPause(t, DateTime.Now));
        var reset = Icon("", "Reset", () => Timers.Reset(t));
        Border card = null!;
        var remove = Icon("", "Delete", () =>
        {
            d.Timers.Remove(t);
            Timers.SaveSoon();
            TimerList.Children.Remove(card);
            updates.Remove(Draw);
        });
        void Draw(DateTime now)
        {
            var left = Timers.Remaining(t, now);
            clock.Text = Timers.Clock(left);
            ((ScaleTransform)fill.RenderTransform).ScaleX = t.Seconds > 0 ? left / t.Seconds : 0;
            play.Content = t.Ends is null ? "" : "";
            play.ToolTip = t.Ends is null ? (t.Left > 0 ? "Resume" : "Start") : "Pause";
            clock.Opacity = t.Ends is null && t.Left == 0 ? 0.6 : 1;
        }
        updates.Add(Draw);
        card = new Border
        {
            Width = 230, Margin = new(0, 0, 12, 12), Padding = new(14, 12, 14, 10), CornerRadius = new(14),
            Background = (Brush)FindResource("Popup"), BorderBrush = (Brush)FindResource("CardEdge"), BorderThickness = new(1),
            Child = new StackPanel
            {
                Children =
                {
                    name, clock, bar, length,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 6, 0, 0), Children = { play, reset, remove } },
                },
            },
        };
        Draw(DateTime.Now);
        return card;
    }

    // ---- stopwatch ----

    void WatchStartStop(object sender, RoutedEventArgs e)
    {
        Timers.StartStopWatch(DateTime.Now);
        Update();
    }

    void WatchLap(object sender, RoutedEventArgs e)
    {
        Timers.Lap(DateTime.Now);
        DrawLaps();
    }

    void WatchReset(object sender, RoutedEventArgs e)
    {
        Timers.ResetWatch();
        DrawLaps();
        Update();
    }

    /// Newest first: lap number, the lap's own time, and the total at that point.
    void DrawLaps()
    {
        LapList.Children.Clear();
        for (var i = d.Laps.Count - 1; i >= 0; i--)
        {
            var split = d.Laps[i] - (i > 0 ? d.Laps[i - 1] : 0);
            var row = new DockPanel { Margin = new(4, 2, 4, 2) };
            var total = new TextBlock { Text = Timers.Watch(d.Laps[i]), Style = (Style)FindResource("Label"), FontFamily = (FontFamily)FindResource("Mono") };
            DockPanel.SetDock(total, Dock.Right);
            row.Children.Add(total);
            row.Children.Add(new TextBlock { Text = $"Lap {i + 1}    {Timers.Watch(split)}", FontFamily = (FontFamily)FindResource("Mono") });
            LapList.Children.Add(row);
        }
    }

    // ---- Pomodoro ----

    void PomoStartPause(object sender, RoutedEventArgs e)
    {
        Timers.PomodoroStartPause(DateTime.Now);
        Update();
    }

    void PomoSkip(object sender, RoutedEventArgs e)
    {
        if (d.Phase == "") return;
        Timers.NextPhase(DateTime.Now);
        Update();
    }

    void PomoStop(object sender, RoutedEventArgs e)
    {
        Timers.PomodoroStop();
        Update();
    }

    /// The four boxes: minutes for work and breaks, rounds between long breaks. Bad input snaps back.
    void PomoLengths(object sender, RoutedEventArgs e)
    {
        static double Minutes(TextBox b, double was) =>
            double.TryParse(b.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v is > 0 and <= 600 ? v : was;
        d.Work = Minutes(WorkBox, d.Work);
        d.ShortBreak = Minutes(ShortBox, d.ShortBreak);
        d.LongBreak = Minutes(LongBox, d.LongBreak);
        d.LongEvery = int.TryParse(EveryBox.Text, out var n) && n is > 0 and < 100 ? n : d.LongEvery;
        WorkBox.Text = d.Work.ToString(CultureInfo.CurrentCulture);
        ShortBox.Text = d.ShortBreak.ToString(CultureInfo.CurrentCulture);
        LongBox.Text = d.LongBreak.ToString(CultureInfo.CurrentCulture);
        EveryBox.Text = d.LongEvery.ToString(CultureInfo.CurrentCulture);
        Timers.SaveSoon();
        Update();
    }
}
