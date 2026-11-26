using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// The home page: the week's due tasks, events and bills, the weather, and today's note. Every part comes from a page
/// that may be off or unused (no Google, no bills, no city): such a part stays out instead of asking to be set up.
public partial class TodayPage : UserControl, IHeader
{
    public FrameworkElement? Header => null;
    public FrameworkElement State { get; } = new TextBlock { Style = Skin.Style("Label") };

    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// One thing on the agenda. Tick: a task's checkbox; Action: the hover button ("Open", "Paid").
    record Item(DateTime At, bool HasTime, string Title, string Caption, Brush Dot, Action? Tick, string ActionText, Action Action, bool Late, bool Green = false);

    // The forecast outlives the page (pages idle for 10 minutes are dropped): fetched when shown and older than 15 minutes.
    static Weather.Forecast? forecast;
    static DateTime fetchedAt;
    static (double?, double?, bool) fetchedFor;
    bool fetching;

    Action? next; // where "Next up" goes

    public TodayPage()
    {
        InitializeComponent();
        // The clock moves things from "next" to "late" and calendar events refresh in the background: redraw each minute while shown.
        var timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Render(), Dispatcher) { IsEnabled = false };
        IsVisibleChanged += async (_, e) =>
        {
            timer.IsEnabled = (bool)e.NewValue;
            if (!timer.IsEnabled) return;
            Render();
            if (Money.Bills.Any(b => b.Currency != Money.Home) && await Rates.Refresh()) Render(); // bills in another currency
        };
        Render();
    }

    void Render()
    {
        var now = DateTime.Now;
        ((TextBlock)State).Text = $"{(now.Hour switch { < 5 => "Good night", < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" })} · {now.ToString("dddd, MMMM d", En)}";
        var items = Items(now);
        RenderKpis(now, items);
        RenderAgenda(now, items);
        RenderWeather();
        RenderNote();
        var side = WeatherCard.Visibility == Visibility.Visible || NoteCard.Visibility == Visibility.Visible;
        Side.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SideCol.MinWidth = side ? 290 : 0;
        SideCol.Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    // ---- What's due: open tasks with a date, calendar events, bills; late ones and the next seven days ----

    List<Item> Items(DateTime now)
    {
        var end = now.Date.AddDays(7);
        var list = new List<Item>();
        var taskDot = Skin.Brush("Accent");
        if (Features.On("Tasks") || Features.On("Notes") || Features.On("Board"))
            foreach (var (row, done) in TasksPage.All())
                if (!done && row.Due is { } d && d < end)
                    list.Add(new(d, true, row.Text.Length > 0 ? row.Text : "(no text)", row.Source, taskDot, row.Tick, "Open", row.Go, d < now));
        if (Features.On("Calendar"))
            foreach (var ev in GoogleCalendar.Upcoming.Where(e => e.End > now && e.Start < end))
                list.Add(new(ev.AllDay ? ev.Start.Date : ev.Start, !ev.AllDay, ev.Title, ev.Location.Length > 0 ? ev.Location : ev.Calendar.Name,
                    new SolidColorBrush(ev.Calendar.Color), null, "Open", () => App.Go("Calendar"), false));
        if (Features.On("Bills"))
            foreach (var b in Money.Bills.Where(b => b.Due < end))
                list.Add(new(b.Due.Date, false, b.Name, $"{b.Kind} · {b.AmountText}", new SolidColorBrush(BillsPage.Look(b.Kind).Hue),
                    null, "Paid", () => { if (!Money.Pay(b)) Dialog.Info("Bills", $"No {b.Currency} rate yet", "Glance needs today's exchange rate to book it in Budget. Connect to the internet and try again."); },
                    b.Due.Date < now.Date, Green: true));
        return list.OrderBy(i => i.At).ThenBy(i => i.HasTime).ToList();
    }

    void RenderKpis(DateTime now, List<Item> items)
    {
        // Due today: tasks only (events aren't "due"; bills have their own cell).
        var tasks = items.Where(i => i.Tick is not null && (i.Late || i.At.Date == now.Date)).ToList();
        var late = tasks.Count(i => i.Late);
        Show(DueCell, Features.On("Tasks") || Features.On("Notes") || Features.On("Board"));
        DueValue.Text = tasks.Count.ToString();
        DueSub.Text = late > 0 ? $"{late} late" : tasks.Count == 0 ? "All clear" : "None late";
        DueSub.Foreground = Skin.Brush(late > 0 ? "Red" : "Sub");

        // Next up: the next timed task or event today.
        var upcoming = items.FirstOrDefault(i => i.HasTime && i.At >= now && i.At.Date == now.Date);
        next = upcoming?.Action; // a task opens where it lives, an event the calendar
        NextValue.Text = upcoming?.At.ToString("HH:mm", En) ?? "—";
        NextSub.Text = upcoming?.Title ?? "Nothing else today";
        NextCell.ToolTip = upcoming is null ? null : upcoming.Title;
        NextCell.Cursor = upcoming is null ? null : Cursors.Hand;

        // Bills: only for someone who keeps them.
        var bills = Money.Bills.Where(b => b.Due.Date < now.Date.AddDays(7)).OrderBy(b => b.Due).ToList();
        Show(BillsCell, Features.On("Bills") && Money.Bills.Count > 0);
        // In the home currency; one in another currency counts at today's rate (left out until there is one).
        BillsValue.Text = Money.Format(bills.Sum(b => Money.InHome(b.Amount, b.Currency) ?? 0), plus: false);
        BillsSub.Text = bills.Count == 0 ? "None due" : $"{bills[0].Name} {bills[0].When(now).ToLowerInvariant()}" + (bills.Count > 1 ? $" · {bills.Count - 1} more" : "");
        BillsSub.Foreground = Skin.Brush(bills.Any(b => b.Due.Date < now.Date) ? "Red" : "Sub");

        // Spent: only for someone who uses Budget.
        var month = Money.Entries.Where(e => e.Date.Year == now.Year && e.Date.Month == now.Month).ToList();
        Show(SpentCell, Features.On("Budget") && Money.Entries.Count > 0);
        SpentCaption.Text = $"Spent in {now.ToString("MMMM", En)}";
        SpentValue.Text = Money.Format(-month.Where(e => e.Amount < 0).Sum(e => e.Amount), plus: false);
        var balance = month.Sum(e => e.Amount);
        SpentSub.Text = $"Balance {Money.Format(balance)}";
        SpentSub.Foreground = Skin.Brush(balance < 0 ? "Red" : "Sub");

        // The first shown cell has no divider on its left.
        var first = true;
        foreach (Border c in Kpis.Children)
        {
            if (c.Visibility != Visibility.Visible) continue;
            c.BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0);
            first = false;
        }
        ((FrameworkElement)Kpis.Parent).Visibility = first ? Visibility.Collapsed : Visibility.Visible;
    }

    static void Show(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    void RenderAgenda(DateTime now, List<Item> items)
    {
        Agenda.Children.Clear();
        string Group(Item i) => i.Late ? "Late" : (i.At.Date - now.Date).Days switch { 0 => "Today", 1 => "Tomorrow", _ => i.At.ToString("dddd, MMM d", En) };
        foreach (var g in items.GroupBy(Group))
        {
            Agenda.Children.Add(new TextBlock
            {
                Text = $"{g.Key}  {g.Count()}", Style = Skin.Style("Label"), FontWeight = FontWeights.SemiBold, Margin = new(2, Agenda.Children.Count == 0 ? 0 : 16, 0, 4),
                Foreground = Skin.Brush(g.Key == "Late" ? "Red" : "Sub"),
            });
            foreach (var i in g) Agenda.Children.Add(Line(i, now));
        }
        var open = items.Count;
        AgendaCount.Text = open == 0 ? "" : $"{open} {(open == 1 ? "item" : "items")}";
        AgendaEmpty.Visibility = open == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<string> from = ["due dates on Notes checklists"];
        if (Features.On("Bills")) from.Add("bills");
        if (Features.On("Calendar") && GoogleCalendar.Connected) from.Add("calendar events");
        var said = from.Count == 1 ? from[0] : string.Join(", ", from[..^1]) + " and " + from[^1];
        AgendaHint.Text = char.ToUpperInvariant(said[0]) + said[1..] + " show up here.";
    }

    /// A row: when, a checkbox or a dot, the title over where it's from, and its action on hover.
    FrameworkElement Line(Item i, DateTime now)
    {
        var when = new TextBlock
        {
            Text = i.Late ? i.At.ToString(i.At.Date == now.Date ? "HH:mm" : "MMM d", En) : i.HasTime ? i.At.ToString("HH:mm", En) : "",
            Width = 58, Style = Skin.Style("Label"), Foreground = Skin.Brush(i.Late ? "Red" : "Sub"), VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        FrameworkElement mark;
        if (i.Tick is { } tick)
        {
            var box = new CheckBox { Style = Skin.Style("Check"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Done" };
            box.Click += (_, _) => { tick(); Render(); };
            mark = box;
        }
        else mark = new Ellipse { Width = 8, Height = 8, Fill = i.Dot, VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 5, 0) };
        mark.Margin = new Thickness(mark.Margin.Left, 0, 12, 0);

        var act = new Button { Style = Skin.Style("Ghost"), Content = i.ActionText, Padding = new(8, 3, 8, 3), Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        if (i.Green) act.Foreground = Skin.Brush("Green");
        act.Click += (_, _) => { i.Action(); Render(); };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = i.Title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = i.Title });
        text.Children.Add(new TextBlock { Text = i.Caption, Style = Skin.Style("Label"), FontSize = 12, Margin = new(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

        DockPanel.SetDock(when, Dock.Left);
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(act, Dock.Right);
        var row = new Border
        {
            Padding = new(8, 7, 4, 7), CornerRadius = new(8), Background = Brushes.Transparent,
            Child = new DockPanel { Children = { when, mark, act, text } },
        };
        void Hover(bool on) { row.Background = on ? Skin.Brush("CardBg") : Brushes.Transparent; act.Opacity = on || row.IsKeyboardFocusWithin ? 1 : 0; }
        row.MouseEnter += (_, _) => Hover(true);
        row.MouseLeave += (_, _) => Hover(false);
        row.IsKeyboardFocusWithinChanged += (_, _) => Hover(row.IsMouseOver);
        return row;
    }

    // ---- Weather: only once a city is set ----

    void RenderWeather()
    {
        var s = App.Settings;
        Show(WeatherCard, Features.On("Weather") && s.Lat is not null && s.Lon is not null);
        if (WeatherCard.Visibility != Visibility.Visible) return;
        if ((s.Lat, s.Lon, s.Fahrenheit) != fetchedFor) forecast = null;
        if (forecast is null || DateTime.Now - fetchedAt > TimeSpan.FromMinutes(15)) _ = Fetch();
        if (forecast is not { } f)
        {
            WxGlyph.Text = WxTemp.Text = "";
            WxText.Text = fetching ? "Loading weather…" : "Weather unavailable";
            WxSub.Text = s.City;
            WxHours.Children.Clear();
            return;
        }
        var c = f.Current;
        var (glyph, text) = Weather.Describe(c.Code, c.IsDay == 1);
        WxGlyph.Text = glyph;
        WxTemp.Text = $"{c.Temp:0}°";
        WxText.Text = text;
        WxSub.Text = $"{s.City} · H {f.Daily.Max[0]:0}°  L {f.Daily.Min[0]:0}°";

        // Every other hour for the next twelve; times are the city's own (timezone=auto), so match against the API's "now".
        WxHours.Children.Clear();
        var start = Math.Max(0, Array.FindIndex(f.Hourly.Time, t => t.StartsWith(c.Time[..13])));
        for (var i = start + 2; i <= start + 12 && i < f.Hourly.Time.Length; i += 2)
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new TextBlock { Text = f.Hourly.Time[i][11..13], Style = Skin.Style("Label"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = Weather.Describe(f.Hourly.Code[i]).Glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 2, 0, 0) });
            cell.Children.Add(new TextBlock { Text = $"{f.Hourly.Temp[i]:0}°", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock
            {
                Text = f.Hourly.Rain[i] is >= 20 and var r ? $"{r}%" : " ", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xA9, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            WxHours.Children.Add(cell);
        }
    }

    async Task Fetch()
    {
        if (fetching) return;
        var s = App.Settings;
        fetching = true;
        fetchedAt = DateTime.Now;
        fetchedFor = (s.Lat, s.Lon, s.Fahrenheit);
        try { forecast = await Weather.Get(s.Lat!.Value, s.Lon!.Value, s.Fahrenheit); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            fetchedAt = default; // try again next time
            Db.Warn($"today weather: {e.Message}");
        }
        finally { fetching = false; }
        RenderWeather();
    }

    // ---- Today's note ----

    void RenderNote()
    {
        Show(NoteCard, Features.On("Notes"));
        var name = DateTime.Today.ToString("yyyy-MM-dd");
        var note = NoteStore.Notes.FirstOrDefault(n => n.Kind == NoteKind.Text && n.Title.Trim() == name); // don't make one just by looking
        var text = note?.Text.Trim() ?? "";
        NoteText.Text = text.Length > 0 ? text : "Nothing yet today.";
        NoteText.Foreground = Skin.Brush(text.Length > 0 ? "Text" : "Sub");
    }

    void JotKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = Jot.Text.Trim();
        if (text.Length == 0) return;
        CaptureWindow.Today(text);
        Jot.Clear();
        RenderNote();
    }

    void OpenNote(object sender, RoutedEventArgs e)
    {
        var note = NoteStore.Today();
        App.Go("Notes");
        NoteStore.RequestFocus(note);
    }

    void GoTasks(object sender, MouseButtonEventArgs e) => App.Go(Features.On("Tasks") ? "Tasks" : "Notes");
    void GoNext(object sender, MouseButtonEventArgs e) => next?.Invoke();
    void GoBills(object sender, MouseButtonEventArgs e) => App.Go("Bills");
    void GoBudget(object sender, MouseButtonEventArgs e) => App.Go("Budget");
    void GoWeather(object sender, MouseButtonEventArgs e) => App.Go("Weather");
}
