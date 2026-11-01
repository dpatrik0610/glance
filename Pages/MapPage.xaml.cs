using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// Leaflet (Map\map.html) in a WebView2, locked to one city or country. Pins are saved; search results are pins not yet saved.
public partial class MapPage : UserControl
{
    const string Unnamed = "Dropped pin";

    record Msg(string T, string? Id, double Lat, double Lng);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly List<object> queued = []; // messages sent before the page loaded
    readonly DispatcherTimer saveSoon;
    List<Pin> results = [];
    bool ready, showingResults, fromMap;

    static Places.Area? Area => App.Settings.MapBounds is [var s, var w, var n, var e] ? new(App.Settings.MapArea, s, w, n, e) : null;

    public MapPage()
    {
        InitializeComponent();
        Swatches.ItemsSource = CliPage.Palette;
        // Typing in name/note: save and relabel the marker once typing pauses.
        saveSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            saveSoon!.Stop();
            PinStore.Save();
            SendPins();
        }, Dispatcher);
        saveSoon.Stop();
        Application.Current.Exit += (_, _) => { if (saveSoon.IsEnabled) PinStore.Save(); };
        PinStore.Pins.CollectionChanged += (_, _) => Title();
        AreaName.Text = Area?.Name ?? "Not set";
        ShowPins();
        ShowDetails(null);
        InitWeb();
    }

    async void InitWeb()
    {
        try
        {
            await WebHost.Open(Web, "glance.map", "Map", "map.html", core =>
            {
                core.WebMessageReceived += (_, e) => Receive(JsonSerializer.Deserialize<Msg>(e.WebMessageAsJson, Json)!);
                core.NewWindowRequested += (_, e) => { e.Handled = true; Browse(e.Uri); }; // attribution links
            });
        }
        catch (Exception e) { Db.Error("map webview", e); } // e.g. WebView2 runtime missing
    }

    void Post(object message)
    {
        if (!ready) { queued.Add(message); return; }
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    void Receive(Msg m)
    {
        switch (m.T)
        {
            case "ready":
                ready = true;
                if (Area is { } a) SendArea(a);
                else if (App.Settings.City is { Length: > 0 } city) SetArea(city); // first visit: the Weather hometown
                else ChangeArea(this, new());
                SendPins();
                foreach (var q in queued) Post(q);
                queued.Clear();
                break;
            case "add":
                Drop(m.Lat, m.Lng);
                break;
            case "move" when Find(m.Id) is { } p:
                (p.Lat, p.Lon) = (m.Lat, m.Lng);
                p.Refresh();
                PinStore.Save();
                break;
            case "select" when Find(m.Id) is { } p:
                if (List.ItemsSource is IEnumerable<Pin> shown && !shown.Contains(p)) ShowPins();
                fromMap = true; // it's already in view: highlight, don't fly
                List.SelectedItem = p;
                List.ScrollIntoView(p);
                fromMap = false;
                break;
            case "palette":
                ((MainWindow)Window.GetWindow(this)).OpenPalette();
                break;
        }
    }

    Pin? Find(string? id) => PinStore.Pins.FirstOrDefault(p => p.Id == id) ?? results.FirstOrDefault(p => p.Id == id);

    /// From the palette: select a pin and fly to it.
    public void ShowPin(Pin p)
    {
        if (showingResults) ShowPins();
        List.SelectedItem = p;
    }

    // ---- Area ----

    void ChangeArea(object sender, RoutedEventArgs e)
    {
        AreaBox.Visibility = Visibility.Visible;
        AreaBox.Clear();
        AreaBox.Focus();
    }

    void AreaKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AreaBox.Text.Trim() is { Length: > 0 } q) SetArea(q);
    }

    async void SetArea(string query)
    {
        var before = AreaName.Text;
        AreaName.Text = "Looking up…";
        try
        {
            if (await Places.FindArea(query) is not { } a)
            {
                AreaName.Text = before;
                ListTitle.Text = $"Can't use \"{query}\": pick a city or a country.";
                return;
            }
            App.Settings.MapArea = a.Name;
            App.Settings.MapBounds = [a.South, a.West, a.North, a.East];
            App.ApplySettings();
            AreaName.Text = a.Name;
            AreaBox.Visibility = Visibility.Collapsed;
            SendArea(a);
            if (showingResults) ShowPins(); // results were for the old area
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            AreaName.Text = before;
            ListTitle.Text = "Area lookup failed: " + e.Message;
        }
    }

    void SendArea(Places.Area a) => Post(new { t = "area", s = a.South, w = a.West, n = a.North, e = a.East });

    // ---- Search ----

    async void SearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SearchBox.Text.Trim() is not { Length: > 0 } q) return;
        if (Area is not { } a) { ListTitle.Text = "Pick an area first (Change, top right)."; return; }
        ListTitle.Text = "Searching…";
        try
        {
            results = await Places.Search(q, a);
            showingResults = true;
            List.ItemsSource = results;
            BackButton.Visibility = Visibility.Visible;
            Empty.Visibility = Visibility.Collapsed;
            ListTitle.Text = results.Count == 0 ? $"Nothing found for \"{q}\" in {a.Name}"
                : $"{results.Count} result{(results.Count == 1 ? "" : "s")} for \"{q}\"";
            SendResults();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ListTitle.Text = "Search failed: " + ex.Message;
        }
    }

    void BackToPins(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        ShowPins();
    }

    void ShowPins()
    {
        showingResults = false;
        results = [];
        SendResults();
        List.ItemsSource = PinStore.Pins;
        BackButton.Visibility = Visibility.Collapsed;
        Title();
    }

    void Title()
    {
        if (showingResults) return;
        var n = PinStore.Pins.Count;
        ListTitle.Text = n == 1 ? "1 pin" : $"{n} pins";
        Empty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Pins ----

    // Right-click on the map: pin it now, then name it after what's there (if the user hasn't named it meanwhile).
    async void Drop(double lat, double lng)
    {
        var pin = new Pin { Name = Unnamed, Lat = lat, Lon = lng, Color = NextColor() };
        Add(pin, fly: false);
        try
        {
            var found = await Places.Describe(pin);
            if (pin.Name == Unnamed && found is not null) pin.Name = found;
            PinStore.Save();
            SendPins();
            if (Details.DataContext == pin) ShowDetails(pin);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { } // stays "Dropped pin"
    }

    void PinIt(object sender, RoutedEventArgs e)
    {
        if (Details.DataContext is not Pin p) return;
        p.Color = NextColor();
        Add(p, fly: false);
    }

    void Add(Pin p, bool fly)
    {
        PinStore.Pins.Insert(0, p);
        PinStore.Save();
        SendPins(fresh: p.Id);
        SendResults(); // a pinned result shows as a pin, not a dot
        if (showingResults && !results.Contains(p)) ShowPins();
        fromMap = !fly;
        List.SelectedItem = p;
        fromMap = false;
        ShowDetails(p); // same item may already be selected (a result just pinned): refresh the buttons
    }

    static string NextColor() => CliPage.Palette[PinStore.Pins.Count % CliPage.Palette.Length];

    void SendPins(string? fresh = null) => Post(new { t = "pins", items = PinStore.Pins.Select(Item).ToArray(), fresh });

    void SendResults() => Post(new { t = "results", items = results.Where(r => !PinStore.Pins.Contains(r)).Select(Item).ToArray() });

    static object Item(Pin p) => new { id = p.Id, name = p.Name, lat = p.Lat, lng = p.Lon, color = p.Color };

    // ---- Details ----

    void Selected(object sender, SelectionChangedEventArgs e)
    {
        var p = List.SelectedItem as Pin;
        ShowDetails(p);
        if (p is not null) Post(new { t = "select", id = p.Id, fly = !fromMap });
    }

    void ShowDetails(Pin? p)
    {
        Details.DataContext = p;
        Details.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        if (p is null) return;
        p.Refresh(); // open/closed is worked out now
        var pinned = PinStore.Pins.Contains(p);
        var mine = pinned ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.Visibility = NoteBox.Visibility = Swatches.Visibility = mine;
        NameBox.IsReadOnly = !pinned;
    }

    Pin? Shown => Details.DataContext as Pin;

    void Edited(object sender, TextChangedEventArgs e)
    {
        if (Shown is { } p && PinStore.Pins.Contains(p)) { saveSoon.Stop(); saveSoon.Start(); }
    }

    void SetColor(object sender, RoutedEventArgs e)
    {
        if (Shown is not { } p) return;
        p.Color = (string)((FrameworkElement)sender).Tag;
        PinStore.Save();
        SendPins();
    }

    void DeletePin(object sender, RoutedEventArgs e)
    {
        if (Shown is not { } p || !Dialog.Confirm("Delete pin", $"Delete \"{p.Name}\"?", "", "Delete")) return;
        PinStore.Pins.Remove(p);
        PinStore.Save();
        SendPins();
        ShowDetails(List.SelectedItem as Pin);
    }

    void CopyPhone(object sender, RoutedEventArgs e) => ClipboardHistory.Copy(Shown?.Phone ?? "");

    void CopyCoords(object sender, RoutedEventArgs e) { if (Shown is { } p) ClipboardHistory.Copy(p.Coords); }

    void OpenWebsite(object sender, RoutedEventArgs e) { if (Shown?.WebsiteUri is { } u) Browse(u.AbsoluteUri); }

    void OpenGoogle(object sender, RoutedEventArgs e) { if (Shown is { } p) Browse(Places.GoogleMaps(p)); }

    static void Browse(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
