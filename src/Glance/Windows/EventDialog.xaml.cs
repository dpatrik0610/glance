using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glance.Services;

namespace Glance;

/// New or existing Google Calendar event. Times are typed the way checklist due dates are ("fri 10", "in 1h"); the line under
/// them says how they were read. Saving and deleting happen here, so errors show next to what caused them.
public partial class EventDialog : Window
{
    static readonly (string Label, int? Minutes)[] Reminders =
        [("Default", null), ("None", -1), ("At start", 0), ("10 min", 10), ("30 min", 30), ("1 hour", 60), ("1 day", 1440)];

    readonly CalEvent? existing;
    CalInfo calendar;
    int? reminder;
    bool changed;

    /// True when something was saved or deleted.
    public static bool Edit(CalEvent? existing, DateTime day)
    {
        var writable = GoogleCalendar.Calendars.Where(c => c.Writable && c.Shown).ToArray();
        if (existing is null && writable.Length == 0)
        {
            Dialog.Info("Calendar", "No calendar you can add to", "Every calendar switched on is read-only for this Google account.");
            return false;
        }
        var d = new EventDialog(existing, day, writable);
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d.changed;
    }

    EventDialog(CalEvent? e, DateTime day, CalInfo[] writable)
    {
        InitializeComponent();
        existing = e;
        calendar = e?.Calendar ?? writable.FirstOrDefault(c => c.Primary) ?? writable[0];
        Label.Text = string.Join(" ", (e is null ? "NEW EVENT" : "EVENT").ToCharArray());

        // An event stays in its calendar: moving between calendars isn't offered.
        foreach (var c in e is null ? writable : [e.Calendar])
        {
            var chip = new RadioButton { Content = c.Name, GroupName = "cal", IsChecked = c == calendar, Style = (Style)FindResource("Chip"), Margin = new(0, 0, 6, 6) };
            chip.Checked += (_, _) => calendar = c;
            CalendarChips.Children.Add(chip);
        }

        var now = DateTime.Now;
        DateTime start, end;
        if (e is null)
        {
            // Today: the next full hour. Another day: 9:00.
            start = day.Date == now.Date ? now.Date.AddHours(now.Hour + 1) : day.Date.AddHours(9);
            end = start.AddHours(1);
        }
        else (start, end) = (e.Start, e.AllDay ? e.End.AddDays(-1) : e.End); // all-day: shown with the last day, not the day after
        TitleBox.Text = e?.Title ?? "";
        AllDayBox.IsChecked = e?.AllDay == true;
        ShowTimes(start, end);
        WhereBox.Text = e?.Location ?? "";
        NotesBox.Text = e?.Description ?? "";

        reminder = e is null || e.UsesDefaultReminders ? null : e.Popups.Length > 0 ? e.Popups.Min() : -1;
        foreach (var (label, minutes) in Reminders)
        {
            var chip = new RadioButton { Content = label, GroupName = "rem", IsChecked = minutes == reminder, Style = (Style)FindResource("Chip"), Margin = new(0, 0, 6, 6) };
            chip.Checked += (_, _) => reminder = minutes;
            ReminderChips.Children.Add(chip);
        }
        if (reminder is > 0 && Reminders.All(r => r.Minutes != reminder)) // one set elsewhere: keep it, as its own chip
        {
            var custom = reminder;
            var chip = new RadioButton { Content = $"{custom} min", GroupName = "rem", IsChecked = true, Style = (Style)FindResource("Chip"), Margin = new(0, 0, 6, 6) };
            chip.Checked += (_, _) => reminder = custom;
            ReminderChips.Children.Add(chip);
        }

        DeleteButton.Visibility = e is not null && e.Calendar.Writable ? Visibility.Visible : Visibility.Collapsed;
        if (e is { Calendar.Writable: false })
        {
            foreach (var box in new Control[] { TitleBox, AllDayBox, StartBox, EndBox, WhereBox, NotesBox }) box.IsEnabled = false;
            ReminderChips.IsEnabled = false;
            SaveButton.Visibility = Visibility.Collapsed;
            Fail($"{e.Calendar.Name} is read-only for this account.");
        }
        TimesChanged(this, new RoutedEventArgs());
        Loaded += (_, _) => { if (e is null) TitleBox.Focus(); };
    }

    void ShowTimes(DateTime start, DateTime end)
    {
        var now = DateTime.Now;
        if (AllDayBox.IsChecked == true)
        {
            StartBox.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            EndBox.Text = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            StartBox.Text = Due.Format(start, now);
            EndBox.Text = end.Date == start.Date ? end.ToString("HH:mm") : Due.Format(end, now);
        }
    }

    /// Start and end as typed, or why not. The end is read from the start: "11:00" is that day, "in 1h" an hour later.
    (DateTime Start, DateTime End)? Times(out string? why)
    {
        why = null;
        var allDay = AllDayBox.IsChecked == true;
        if (Due.Parse(StartBox.Text, DateTime.Now) is not { } s) { why = "Can't read the start: try \"fri 10\" or \"2026-12-24 18:00\"."; return null; }
        if (allDay) s = s.Date;
        if (Due.Parse(EndBox.Text, allDay ? s.AddMinutes(-1) : s) is not { } e) { why = "Can't read the end: try \"11:00\" or \"in 1h\"."; return null; }
        if (allDay) e = e.Date;
        if (allDay ? e < s : e <= s) { why = "It ends before it starts."; return null; }
        return (s, e);
    }

    void TimesChanged(object sender, RoutedEventArgs e)
    {
        if (Times(out var why) is not { } t) { WhenText.Text = why; return; }
        var culture = CultureInfo.CurrentCulture;
        WhenText.Text = AllDayBox.IsChecked == true
            ? (t.End == t.Start ? t.Start.ToString("dddd, d MMMM", culture) : $"{t.Start.ToString("ddd d MMM", culture)} – {t.End.ToString("ddd d MMM", culture)}")
            : $"{t.Start.ToString("ddd d MMM, HH:mm", culture)} – {(t.End.Date == t.Start.Date ? t.End.ToString("HH:mm") : t.End.ToString("ddd d MMM, HH:mm", culture))}"
              + $"  ({Length(t.End - t.Start)})";
        if (existing?.Recurring == true) WhenText.Text += " · repeats: only this one changes";
    }

    static string Length(TimeSpan d) => d.TotalHours >= 24 ? $"{d.TotalDays:0.#} days" : d.TotalMinutes >= 60 ? $"{d.TotalHours:0.#} h" : $"{d.TotalMinutes:0} min";

    void AllDayToggled(object sender, RoutedEventArgs e)
    {
        // Keep the same days; a timed event gets 9:00–10:00.
        var allDay = AllDayBox.IsChecked == true;
        var day = Due.Parse(StartBox.Text, DateTime.Now)?.Date ?? DateTime.Today;
        ShowTimes(allDay ? day : day.AddHours(9), allDay ? day : day.AddHours(10));
        TimesChanged(sender, e);
    }

    async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (Times(out var why) is not { } t) { Fail(why!); return; }
        var draft = new EventDraft(TitleBox.Text.Trim(), calendar, t.Start, t.End, AllDayBox.IsChecked == true, WhereBox.Text.Trim(),
            NotesBox.Text.Trim(), reminder);
        await Run(() => GoogleCalendar.Save(existing, draft));
    }

    async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (existing is null) return;
        var what = existing.Recurring ? "This occurrence is removed; the others stay." : "It's removed from Google Calendar too.";
        if (!Dialog.Confirm("Delete event", $"Delete \"{existing.Title}\"?", what, "Delete")) return;
        await Run(() => GoogleCalendar.Delete(existing));
    }

    async Task Run(Func<Task> work)
    {
        IsEnabled = false;
        Error.Visibility = Visibility.Collapsed;
        try
        {
            await work();
            changed = true;
            Close();
        }
        catch (Exception ex) when (GoogleCalendar.IsGoogleError(ex))
        {
            Fail(GoogleCalendar.Why(ex));
            IsEnabled = true;
        }
    }

    void Fail(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }

    void CancelClick(object sender, RoutedEventArgs e) => Close();

    void Drag(object sender, MouseButtonEventArgs e) => DragMove();
}
