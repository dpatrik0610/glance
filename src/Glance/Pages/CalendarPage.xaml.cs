using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Glance.Services;

namespace Glance.Pages;

/// Google Calendar month view, with Glance's own due items (notes and stickies) on the same days.
public partial class CalendarPage : UserControl
{
    static readonly CultureInfo Culture = CultureInfo.CurrentCulture;
    static readonly Color DueColor = Color.FromRgb(0xE6, 0xA6, 0x5D);

    DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime day = DateTime.Today;
    List<CalEvent> events = [];
    DateTime? syncedAt;
    DateTime loadedAt;
    int loadId;

    public CalendarPage()
    {
        InitializeComponent();
        ClientIdBox.Text = App.Settings.GoogleClientId;
        ClientSecretBox.Text = App.Settings.GoogleClientSecret;
        // Due items can change on other pages; Google every 5 minutes at most unless asked.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            Render();
            if (DateTime.Now - loadedAt > TimeSpan.FromMinutes(5)) Load();
        };
    }

    /// The first day in the grid: the week the month starts in, by this PC's first day of the week.
    DateTime GridStart => month.AddDays(-(((int)month.DayOfWeek - (int)Culture.DateTimeFormat.FirstDayOfWeek + 7) % 7));

    async void Load()
    {
        var id = ++loadId;
        loadedAt = DateTime.Now;
        if (!await GoogleCalendar.Resume())
        {
            ShowConnected(false);
            Render();
            return;
        }
        ShowConnected(true);
        Status.Text = "Syncing…";
        try
        {
            var list = await GoogleCalendar.Events(GridStart, GridStart.AddDays(42));
            if (id != loadId) return; // another month was picked meanwhile
            (events, syncedAt) = (list, DateTime.Now);
            Status.Text = $"Synced {syncedAt:HH:mm}";
            ShowCalendars();
        }
        catch (Exception e) when (GoogleCalendar.IsGoogleError(e))
        {
            if (id != loadId) return;
            Status.Text = GoogleCalendar.Why(e) + (syncedAt is { } t ? $" · showing {t:HH:mm}" : "");
            loadedAt = default; // try again next time the page shows
            if (!GoogleCalendar.Connected) ShowConnected(false);
        }
        Render();
    }

    void ShowConnected(bool on)
    {
        ConnectCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = RefreshButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = on;
        if (!on) Status.Text = "";
        if (!on) CalendarsPanel.Visibility = Visibility.Collapsed;
    }

    // One switch per calendar. Off: its events go at once; on: they come with the next sync, started right away.
    void ShowCalendars()
    {
        CalendarList.Children.Clear();
        foreach (var c in GoogleCalendar.Calendars)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(c.Color), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = c.Name, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = c.Writable ? c.Name : c.Name + " (read-only)" });
            var toggle = new CheckBox { Style = Skin.Style("Switch"), Content = label, IsChecked = c.Shown, Margin = new(0, 0, 0, 6) };
            toggle.Click += (_, _) =>
            {
                App.Settings.CalendarsShown[c.Id] = toggle.IsChecked == true;
                App.ApplySettings();
                if (toggle.IsChecked != true)
                {
                    events.RemoveAll(e => e.Calendar.Id == c.Id);
                    Render();
                }
                Changed();
            };
            CalendarList.Children.Add(toggle);
        }
        CalendarsPanel.Visibility = GoogleCalendar.Calendars.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static IEnumerable<(Note Note, TodoItem Item)> DueOn(DateTime d) =>
        from n in NoteStore.Everything
        from i in n.Items
        where i.Due?.Date == d
        orderby i.Due
        select (n, i);

    // ---- The month ----

    void Render()
    {
        MonthText.Text = Culture.TextInfo.ToTitleCase(month.ToString("MMMM yyyy", Culture));
        Month.Children.Clear();
        Month.ColumnDefinitions.Clear();
        Month.RowDefinitions.Clear();
        for (var c = 0; c < 7; c++) Month.ColumnDefinitions.Add(new ColumnDefinition());
        Month.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < 6; r++) Month.RowDefinitions.Add(new RowDefinition());

        var start = GridStart;
        for (var c = 0; c < 7; c++)
        {
            var name = new TextBlock { Text = Culture.DateTimeFormat.GetAbbreviatedDayName(start.AddDays(c).DayOfWeek), Style = Label, Margin = new(8, 0, 0, 6) };
            Grid.SetColumn(name, c);
            Month.Children.Add(name);
        }
        for (var i = 0; i < 42; i++)
        {
            var cell = Cell(start.AddDays(i));
            Grid.SetRow(cell, i / 7 + 1);
            Grid.SetColumn(cell, i % 7);
            Month.Children.Add(cell);
        }
        ShowDay();
    }

    Style Label => Skin.Style("Label");

    FrameworkElement Cell(DateTime d)
    {
        var inMonth = d.Month == month.Month;
        var stack = new StackPanel();
        var number = new TextBlock { Text = d.Day.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new(11), Margin = new(0, 0, 0, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Background = d == DateTime.Today ? Skin.Brush("Accent") : Brushes.Transparent, Child = number,
        });

        var chips = events.Where(e => e.On(d)).Select(e => (e.AllDay ? "" : e.Start.Date == d ? e.Start.ToString("HH:mm") + " " : "", e.Title, e.Calendar.Color))
            .Concat(DueOn(d).Select(x => (x.Item.Due!.Value.ToString("HH:mm") + " ", (x.Item.Done ? "✓ " : "") + x.Item.Text, DueColor))).ToList();
        // Three lines fit a cell: three chips, or two and how many more.
        var shown = chips.Count > 3 ? 2 : chips.Count;
        foreach (var (time, title, color) in chips.Take(shown)) stack.Children.Add(Chip(time, title, color));
        if (chips.Count > shown) stack.Children.Add(new TextBlock { Text = $"+{chips.Count - shown} more", Style = Label, FontSize = 10.5, Margin = new(4, 2, 0, 0) });

        var selected = d == day;
        var cell = new Border
        {
            Child = stack, CornerRadius = new(10), Padding = new(5), Margin = new(2), Cursor = Cursors.Hand, ClipToBounds = true,
            Background = inMonth ? Skin.Brush("CardBg") : Brushes.Transparent, Opacity = inMonth ? 1 : 0.45,
            BorderThickness = new(1.5), BorderBrush = selected ? Skin.Brush("Accent") : Brushes.Transparent,
            ToolTip = chips.Count > 3 ? string.Join("\n", chips.Select(c => c.Item1 + c.Item2)) : null,
        };
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && GoogleCalendar.Connected) { Select(d); AddClick(this, e); return; }
            Select(d);
        };
        return cell;
    }

    static FrameworkElement Chip(string time, string title, Color color) => new Border
    {
        CornerRadius = new(4), Margin = new(0, 1, 0, 0), Padding = new(5, 1, 4, 1),
        Background = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)),
        BorderBrush = new SolidColorBrush(color), BorderThickness = new(2, 0, 0, 0),
        Child = new TextBlock { Text = time + title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
    };

    void Select(DateTime d)
    {
        day = d;
        if (d.Month != month.Month || d.Year != month.Year)
        {
            month = new DateTime(d.Year, d.Month, 1);
            Load();
        }
        Render();
    }

    // ---- The selected day ----

    void ShowDay()
    {
        DayText.Text = Culture.TextInfo.ToTitleCase(day.ToString("dddd", Culture));
        DaySub.Text = day.ToString("d MMMM yyyy", Culture) + (day == DateTime.Today ? " · today" : "");
        Agenda.Children.Clear();
        foreach (var e in events.Where(e => e.On(day))) Agenda.Children.Add(EventRow(e));
        foreach (var (note, item) in DueOn(day)) Agenda.Children.Add(DueRow(note, item));
        if (Agenda.Children.Count == 0)
            Agenda.Children.Add(new TextBlock { Text = GoogleCalendar.Connected ? "Nothing on this day. Double-click a day to add an event." : "Nothing due this day.",
                Style = Label, TextWrapping = TextWrapping.Wrap });
    }

    FrameworkElement EventRow(CalEvent e)
    {
        string when;
        if (e.AllDay) when = e.End - e.Start > TimeSpan.FromDays(1) ? $"All day · until {e.End.AddDays(-1).ToString("ddd d MMM", Culture)}" : "All day";
        else if (e.Start.Date == day && e.End.Date == day) when = $"{e.Start:HH:mm} – {e.End:HH:mm}";
        else when = e.Start.Date == day ? $"from {e.Start:HH:mm}" : e.End.Date == day ? $"until {e.End:HH:mm}" : "all day (continues)";
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = when + (e.Recurring ? "  ·  repeats" : ""), Style = Label, FontSize = 11.5 });
        text.Children.Add(new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 0) });
        if (e.Location.Length > 0) text.Children.Add(new TextBlock { Text = e.Location, Style = Label, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = e.Calendar.Name, Style = Label, FontSize = 10.5, Margin = new(0, 2, 0, 0) });
        var row = Row(e.Calendar.Color, text);
        row.Cursor = Cursors.Hand;
        row.ToolTip = e.Calendar.Writable ? "Edit" : "Details (read-only calendar)";
        row.MouseLeftButtonUp += (_, _) => { if (EventDialog.Edit(e, day)) Changed(); };
        return row;
    }

    FrameworkElement DueRow(Note note, TodoItem item)
    {
        var check = new CheckBox { Style = Skin.Style("Check"), DataContext = item, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 8, 0) };
        check.SetBinding(ToggleButton_IsChecked, new Binding(nameof(TodoItem.Done)));
        check.Click += (_, _) => Render();
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"Due {item.Due:HH:mm}" + (item.Remind ? "  ·  🔔" : ""), Style = Label, FontSize = 11.5 });
        text.Children.Add(new TextBlock { Text = item.Text.Length > 0 ? item.Text : "(no text)", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 2, 0, 0), Opacity = item.Done ? 0.45 : 1 });
        text.Children.Add(new TextBlock { Text = (note is Sticky ? "Sticky · " : "Note · ") + note.DisplayTitle, Style = Label, FontSize = 10.5, Margin = new(0, 2, 0, 0) });
        var body = new DockPanel();
        DockPanel.SetDock(check, Dock.Left);
        body.Children.Add(check);
        body.Children.Add(text);
        return Row(DueColor, body);
    }

    static readonly DependencyProperty ToggleButton_IsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    Border Row(Color color, UIElement content)
    {
        var bar = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(color), Margin = new(0, 0, 10, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        dock.Children.Add(bar);
        dock.Children.Add(content);
        return new Border { Child = dock, CornerRadius = new(10), Padding = new(10, 8, 10, 8), Margin = new(0, 0, 0, 6), Background = Skin.Brush("CardBg") };
    }

    // ---- Buttons ----

    void PrevClick(object sender, RoutedEventArgs e) => Go(month.AddMonths(-1));
    void NextClick(object sender, RoutedEventArgs e) => Go(month.AddMonths(1));

    void TodayClick(object sender, RoutedEventArgs e)
    {
        day = DateTime.Today;
        Go(new DateTime(day.Year, day.Month, 1));
    }

    void Go(DateTime first)
    {
        month = first;
        if (day.Month != month.Month || day.Year != month.Year) day = month; // keep a day of the shown month selected
        Render();
        Load();
    }

    void RefreshClick(object sender, RoutedEventArgs e) => Load();

    void AddClick(object sender, RoutedEventArgs e)
    {
        if (EventDialog.Edit(null, day)) Changed();
    }

    // After an add, edit or delete: the month again, and the reminder list so a new event's popup isn't missed.
    void Changed()
    {
        Load();
        _ = GoogleCalendar.RefreshUpcoming();
    }

    async void ConnectClick(object sender, RoutedEventArgs e)
    {
        App.Settings.GoogleClientId = ClientIdBox.Text.Trim();
        App.Settings.GoogleClientSecret = ClientSecretBox.Text.Trim();
        App.ApplySettings();
        if (!GoogleCalendar.HasClient) { ConnectStatus.Text = "Paste both the client ID and the secret."; return; }
        ConnectButton.IsEnabled = false;
        ConnectStatus.Text = "Waiting for you in the browser…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await GoogleCalendar.Connect(timeout.Token);
            ConnectStatus.Text = "";
            Load();
            _ = GoogleCalendar.RefreshUpcoming();
        }
        catch (OperationCanceledException) { ConnectStatus.Text = "No answer from the browser in 3 minutes. Try again."; }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
        {
            ConnectStatus.Text = ex.Error?.Error switch
            {
                "access_denied" => "You declined on Google's page.",
                "invalid_client" => "Google doesn't know that client ID and secret: check them.",
                var other => $"Google refused: {ex.Error?.ErrorDescription ?? other}",
            };
        }
        catch (Exception ex) when (GoogleCalendar.IsGoogleError(ex)) { ConnectStatus.Text = GoogleCalendar.Why(ex); }
        finally { ConnectButton.IsEnabled = true; }
    }

    async void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm("Calendar", "Disconnect Google Calendar?", "Glance forgets the sign-in on this PC. Your calendar itself isn't touched.",
                "Disconnect", danger: false)) return;
        await GoogleCalendar.Disconnect();
        events = [];
        syncedAt = null;
        ShowConnected(false);
        Render();
    }
}
