using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Glance.Services;

/// A place on the map: a saved pin, or a search result (same shape, not in PinStore until pinned).
public class Pin : Bindable
{
    string name = "", note = "", color = "#FF6A3D";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => name; set => Set(ref name, value); }
    public string Note { get => note; set => Set(ref note, value); }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Color { get => color; set { if (Set(ref color, value)) Raise(nameof(Brush)); } }
    public DateTime Created { get; set; } = DateTime.Now;
    // From OpenStreetMap, when the place is a shop, restaurant and so on.
    public string? Kind { get; set; }
    public string? Address { get; set; }
    public string? Hours { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }

    // "Color" inside this class is the string property, hence the full type name.
    [JsonIgnore] public Brush Brush => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color));
    [JsonIgnore] public string Coords => Places.Coords(Lat, Lon);
    [JsonIgnore] public string Subtitle => string.Join(" · ", new[] { Kind, Address }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } s ? s : Coords;

    // Worked out when shown, so it's current whenever the list or details refresh.
    [JsonIgnore] public string? OpenText => OpeningHours.Status(Hours, DateTime.Now, out _);
    [JsonIgnore] public bool IsOpen => OpeningHours.Status(Hours, DateTime.Now, out var open) is not null && open;
    [JsonIgnore] public bool HasHours => OpenText is not null;
    [JsonIgnore] public string OpenShort => !HasHours ? "" : IsOpen ? "Open" : "Closed";
    [JsonIgnore] public Brush OpenBrush => (Brush)Application.Current.Resources[IsOpen ? "Green" : "Red"];
    [JsonIgnore] public bool HasPhone => !string.IsNullOrEmpty(Phone);
    [JsonIgnore] public bool HasWebsite => WebsiteUri is not null;
    // Only http(s) ever opens: the value comes from OpenStreetMap. "example.com" (no scheme, common in OSM) gets https.
    [JsonIgnore] public Uri? WebsiteUri => string.IsNullOrWhiteSpace(Website) ? null
        : Uri.TryCreate(Website.Contains("://") ? Website : "https://" + Website, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    [JsonIgnore] public string WebsiteHost => WebsiteUri?.Host.Replace("www.", "") ?? "";

    /// Everything about it may have changed (renamed by lookup, moved, details filled in).
    public void Refresh() => Raise("");
}

/// Map pins in %APPDATA%\Glance\pins.json. Local only: never in the repo or Mongo.
public static class PinStore
{
    static readonly string File_ = Path.Combine(Settings.Dir, "pins.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static ObservableCollection<Pin>? pins;

    public static ObservableCollection<Pin> Pins => pins ??= Load();

    static ObservableCollection<Pin> Load()
    {
        try { return JsonSerializer.Deserialize<ObservableCollection<Pin>>(File.ReadAllText(File_), Json) ?? []; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (JsonException e)
        {
            // Never overwrite a file we couldn't read: park it.
            var parked = $"{File_}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(File_, parked);
            Db.Error($"pins.json unreadable, moved to {parked}", e);
            return [];
        }
    }

    public static void Save()
    {
        try { Settings.WriteAtomic(File_, JsonSerializer.Serialize(Pins, Json)); }
        catch (IOException e) { Db.Warn($"pins save: {e.Message}"); }
    }
}

/// Place search via OpenStreetMap's Nominatim: free, no key. One request per user action (their policy: at most 1/s).
public static class Places
{
    static readonly HttpClient http = Client();

    public record Area(string Name, double South, double West, double North, double East);

    record Hit(string Lat, string Lon, string? Name, string? Type, string? Addresstype,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        string[]? Boundingbox, Dictionary<string, string>? Address, Dictionary<string, string>? Extratags);

    static HttpClient Client()
    {
        var c = new HttpClient { BaseAddress = new("https://nominatim.openstreetmap.org/"), Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Glance/1.0 (personal desktop dashboard)"); // Nominatim requires an identifying agent
        return c;
    }

    /// A city or country to lock the map to. Null if not found or bigger than a country (a continent, an ocean).
    public static async Task<Area?> FindArea(string query)
    {
        var hits = await http.GetFromJsonAsync<Hit[]>($"search?format=jsonv2&limit=1&q={Uri.EscapeDataString(query)}");
        if (hits is not [{ Boundingbox: [var s, var n, var w, var e] } h]) return null;
        var a = new Area(NameOf(h), Num(s), Num(w), Num(n), Num(e));
        return a.North - a.South > 40 || a.East - a.West > 60 ? null : a;
    }

    /// Up to 15 places matching the text inside the area: "pizza", "pharmacy", "Lidl", a street.
    public static async Task<List<Pin>> Search(string query, Area area)
    {
        var box = FormattableString.Invariant($"{area.West},{area.North},{area.East},{area.South}");
        var hits = await http.GetFromJsonAsync<Hit[]>(
            $"search?format=jsonv2&limit=15&addressdetails=1&extratags=1&bounded=1&viewbox={box}&q={Uri.EscapeDataString(query)}");
        return (hits ?? []).Select(h => Fill(new Pin { Lat = Num(h.Lat), Lon = Num(h.Lon), Color = "#F4F4F5" }, h)).ToList();
    }

    /// What's at a dropped pin: fills in address, hours and contacts, returns a name for it (null over open water and such).
    public static async Task<string?> Describe(Pin p)
    {
        var h = await http.GetFromJsonAsync<Hit>(FormattableString.Invariant(
            $"reverse?format=jsonv2&zoom=18&addressdetails=1&extratags=1&lat={p.Lat}&lon={p.Lon}"));
        if (h?.DisplayName is null) return null;
        Fill(p, h, keepName: true);
        return NameOf(h);
    }

    static Pin Fill(Pin p, Hit h, bool keepName = false)
    {
        if (!keepName) p.Name = NameOf(h);
        var tags = h.Extratags ?? [];
        var kind = h.Type is null or "yes" ? null : Cap(h.Type.Replace('_', ' '));
        if (tags.GetValueOrDefault("cuisine") is { } cuisine) kind = $"{kind ?? "Food"} · {cuisine.Replace(';', ',').Replace('_', ' ')}";
        p.Kind = kind;
        p.Address = Street(h.Address);
        p.Hours = tags.GetValueOrDefault("opening_hours");
        p.Phone = tags.GetValueOrDefault("phone") ?? tags.GetValueOrDefault("contact:phone");
        p.Website = tags.GetValueOrDefault("website") ?? tags.GetValueOrDefault("contact:website");
        p.Refresh();
        return p;
    }

    // "Andrássy út 12, Budapest"
    static string? Street(Dictionary<string, string>? a)
    {
        if (a is null) return null;
        var street = string.Join(" ", new[] { a.GetValueOrDefault("road"), a.GetValueOrDefault("house_number") }.Where(s => s is not null));
        var town = a.GetValueOrDefault("city") ?? a.GetValueOrDefault("town") ?? a.GetValueOrDefault("village");
        return string.Join(", ", new[] { street, town }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } s ? s : null;
    }

    static string NameOf(Hit h) => !string.IsNullOrWhiteSpace(h.Name) ? h.Name : ShortName(h.DisplayName ?? "");

    /// "12, Andrássy út, Terézváros, Budapest, …, Magyarország" → "12, Andrássy út": the two most specific parts.
    public static string ShortName(string displayName) => string.Join(", ", displayName.Split(", ").Take(2));

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // Always invariant: a Hungarian locale would write 47,5 and break URLs and copy-paste.
    public static string Coords(double lat, double lon) => FormattableString.Invariant($"{lat:0.00000}, {lon:0.00000}");

    /// Google Maps for the place itself, where its reviews and photos are. Searches by name and address when known.
    public static string GoogleMaps(Pin p) => p.Kind is null && p.Address is null
        ? FormattableString.Invariant($"https://www.google.com/maps/search/?api=1&query={p.Lat},{p.Lon}")
        : $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString($"{p.Name}, {p.Address}")}";

    static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
