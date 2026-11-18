using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Glance.Services;

public class TimerItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// Its length; when running, when it ends; when paused, what's left (0 = not started).
    public double Seconds { get; set; } = 300;
    public DateTime? Ends { get; set; }
    public double Left { get; set; }
}

/// Everything on the Timer page, in timers.json, so it all keeps going across restarts.
public class TimerData
{
    public List<TimerItem> Timers { get; set; } = [];
    /// Stopwatch: time banked while stopped, when the running stretch began, and laps (total seconds at each).
    public double WatchSeconds { get; set; }
    public DateTime? WatchStarted { get; set; }
    public List<double> Laps { get; set; } = [];
    /// Pomodoro, in minutes; a long break after every LongEvery work rounds.
    public double Work { get; set; } = 25;
    public double ShortBreak { get; set; } = 5;
    public double LongBreak { get; set; } = 15;
    public int LongEvery { get; set; } = 4;
    /// "" (off), work, short, long. Running: PhaseEnds; paused: PhaseLeft.
    public string Phase { get; set; } = "";
    public DateTime? PhaseEnds { get; set; }
    public double PhaseLeft { get; set; }
    /// Work rounds finished in this session.
    public int Done { get; set; }
}

/// Timers, stopwatch and Pomodoro. They tick here (from startup), not on the page, so alerts come whether it's open or not.
public static class Timers
{
    static readonly string File_ = Path.Combine(Settings.Dir, "timers.json");
    static TimerData? data;
    static DispatcherTimer? saveTimer;

    public static TimerData Data => data ??= Load();

    static TimerData Load()
    {
        try { return JsonSerializer.Deserialize<TimerData>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public static void Save()
    {
        saveTimer?.Stop();
        if (data is not null) Settings.WriteAtomic(File_, JsonSerializer.Serialize(data));
    }

    public static void SaveSoon()
    {
        if (saveTimer is null)
        {
            saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
            saveTimer.Tick += (_, _) => Save();
            Application.Current.Exit += (_, _) => { if (saveTimer.IsEnabled) Save(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// At startup: timers left running across a restart alert on time (or right away, if they ended meanwhile).
    public static void Start()
    {
        data ??= Load();
        // The constructor with a handler starts it, and the dispatcher keeps it alive.
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Check(DateTime.Now), Application.Current.Dispatcher);
    }

    static void Check(DateTime now)
    {
        foreach (var t in Data.Timers.Where(t => t.Ends <= now))
        {
            (t.Ends, t.Left) = (null, 0);
            SaveSoon();
            Alert(t.Name.Trim() == "" ? "Timer" : $"Timer · {t.Name}", "Time's up", "Length", Clock(t.Seconds));
        }
        if (Data.PhaseEnds <= now) NextPhase(now, alert: true);
    }

    static void Alert(string label, string headline, string side, string sideText) =>
        AlertWindow.Show(new Toast(label, headline, "", side, sideText, ((SolidColorBrush)Application.Current.Resources["Accent"]).Color, true),
            () => App.Go("Timer"));

    // ---- timers ----

    public static double Remaining(TimerItem t, DateTime now) =>
        t.Ends is { } e ? Math.Max(0, (e - now).TotalSeconds) : t.Left > 0 ? t.Left : t.Seconds;

    public static void StartPause(TimerItem t, DateTime now)
    {
        if (t.Ends is not null) (t.Left, t.Ends) = (Remaining(t, now), null);
        else (t.Ends, t.Left) = (now.AddSeconds(Remaining(t, now)), 0);
        SaveSoon();
    }

    public static void Reset(TimerItem t)
    {
        (t.Ends, t.Left) = (null, 0);
        SaveSoon();
    }

    // ---- stopwatch ----

    public static double Elapsed(DateTime now) => Data.WatchSeconds + (Data.WatchStarted is { } s ? (now - s).TotalSeconds : 0);

    public static void StartStopWatch(DateTime now)
    {
        if (Data.WatchStarted is null) Data.WatchStarted = now;
        else (Data.WatchSeconds, Data.WatchStarted) = (Elapsed(now), null);
        SaveSoon();
    }

    public static void Lap(DateTime now)
    {
        Data.Laps.Add(Elapsed(now));
        SaveSoon();
    }

    public static void ResetWatch()
    {
        (Data.WatchSeconds, Data.WatchStarted) = (0, null);
        Data.Laps.Clear();
        SaveSoon();
    }

    // ---- Pomodoro ----

    public static double PhaseLength(string phase) => 60 * phase switch { "short" => Data.ShortBreak, "long" => Data.LongBreak, _ => Data.Work };

    public static double PhaseRemaining(DateTime now) =>
        Data.PhaseEnds is { } e ? Math.Max(0, (e - now).TotalSeconds) : Data.PhaseLeft > 0 ? Data.PhaseLeft : PhaseLength(Data.Phase);

    /// Starts a session with a work round, or pauses / resumes the current phase.
    public static void PomodoroStartPause(DateTime now)
    {
        var d = Data;
        if (d.Phase == "") (d.Phase, d.Done, d.PhaseLeft) = ("work", 0, 0);
        if (d.PhaseEnds is not null) (d.PhaseLeft, d.PhaseEnds) = (PhaseRemaining(now), null);
        else (d.PhaseEnds, d.PhaseLeft) = (now.AddSeconds(PhaseRemaining(now)), 0);
        SaveSoon();
    }

    public static void PomodoroStop()
    {
        (Data.Phase, Data.PhaseEnds, Data.PhaseLeft, Data.Done) = ("", null, 0, 0);
        SaveSoon();
    }

    /// Work → a break (long after every LongEvery rounds) → work…, starting the next one at once.
    public static void NextPhase(DateTime now, bool alert = false)
    {
        var d = Data;
        if (d.Phase == "") return;
        string next;
        if (d.Phase == "work")
        {
            d.Done++;
            next = d.LongEvery > 0 && d.Done % d.LongEvery == 0 ? "long" : "short";
        }
        else next = "work";
        (d.Phase, d.PhaseLeft) = (next, 0);
        d.PhaseEnds = now.AddSeconds(PhaseLength(next));
        SaveSoon();
        if (alert)
            Alert("Pomodoro", next == "work" ? "Break's over" : "Time for a break", next == "work" ? "Work" : "Break", Clock(PhaseLength(next)));
    }

    // ---- lengths ----

    /// "25" = 25 minutes, "1:30" = 1 min 30 s, "1:00:00" = an hour. Null when it isn't a positive length.
    public static double? ParseDuration(string s)
    {
        var parts = s.Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => !uint.TryParse(p, out _))) return null;
        var n = parts.Select(double.Parse).ToArray();
        var secs = n.Length switch { 1 => n[0] * 60, 2 => n[0] * 60 + n[1], _ => n[0] * 3600 + n[1] * 60 + n[2] };
        return secs > 0 ? secs : null;
    }

    public static string Clock(double secs)
    {
        var t = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, secs)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    /// A stopwatch reading, with tenths.
    public static string Watch(double secs)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, secs));
        return (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss")) + $".{t.Milliseconds / 100}";
    }
}
