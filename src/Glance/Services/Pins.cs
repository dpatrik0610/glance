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
    [JsonIgnore] public Brush OpenBrush => Skin.Brush(IsOpen ? "Green" : "Red");
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

    static ObservableCollection<Pin> Load() => Settings.ReadJson<ObservableCollection<Pin>>(File_, Json) ?? [];

    public static void Save()
    {
        try { Settings.WriteAtomic(File_, JsonSerializer.Serialize(Pins, Json)); }
        catch (IOException e) { Db.Warn($"pins save: {e.Message}"); }
    }
}

/// Place lookups in OpenStreetMap data: Nominatim (names, addresses) and Overpass (tags: cuisine, shop type).
/// Both free, no key; one request per user action, as their policies ask.
public static class Places
{
    static readonly HttpClient http = Client("https://nominatim.openstreetmap.org/");
    static readonly HttpClient[] Overpass = [Client("https://overpass-api.de/api/", 8), Client("https://overpass.private.coffee/api/", 8)];

    public record Area(string Name, double South, double West, double North, double East);

    record Hit(string Lat, string Lon, string? Name, string? Type, string? Addresstype,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        string[]? Boundingbox, Dictionary<string, string>? Address, Dictionary<string, string>? Extratags);

    record Osm(OsmElement[] Elements);
    record OsmElement(double? Lat, double? Lon, Coord? Center, Dictionary<string, string>? Tags);
    record Coord(double Lat, double Lon);

    static HttpClient Client(string root, int seconds = 15)
    {
        var c = new HttpClient { BaseAddress = new(root), Timeout = TimeSpan.FromSeconds(seconds) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Glance/1.0 (personal desktop dashboard)"); // both ask for an identifying agent
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

    /// Places in the area matching the text, closest to its middle first. Tags first ("pizza" finds cuisine=pizza and
    /// "Pizzéria"; "pharmacy", "Lidl"), then names and addresses (a street) when the tags find nothing.
    /// TagsBusy: the tag search couldn't run (servers full), so these are name matches only.
    public static async Task<(List<Pin> Places, bool TagsBusy)> Search(string query, Area area)
    {
        List<Pin>? found = null;
        if (area.North - area.South < 1) // county-sized or smaller: a tag search over a whole country is slow on a shared server
            found = await Tagged(query, area);
        var busy = found is null && area.North - area.South < 1;
        if (found is not { Count: > 0 }) found = await Named(query, area);
        double lat = (area.North + area.South) / 2, lon = (area.East + area.West) / 2, squeeze = Math.Cos(lat * Math.PI / 180);
        return (found.OrderBy(p => Math.Pow(p.Lat - lat, 2) + Math.Pow((p.Lon - lon) * squeeze, 2)).ToList(), busy); // flat-earth distance: fine at city scale
    }

    static async Task<List<Pin>> Named(string query, Area a)
    {
        var box = FormattableString.Invariant($"{a.West},{a.North},{a.East},{a.South}");
        var hits = await http.GetFromJsonAsync<Hit[]>(
            $"search?format=jsonv2&limit=15&addressdetails=1&extratags=1&bounded=1&viewbox={box}&q={Uri.EscapeDataString(query)}");
        return (hits ?? []).Select(h => Fill(new Pin { Lat = Num(h.Lat), Lon = Num(h.Lon), Color = "#F4F4F5" }, h)).ToList();
    }

    /// Null when every server was full or down.
    static async Task<List<Pin>?> Tagged(string query, Area a)
    {
        var re = Pattern(query).Replace(@"\", @"\\").Replace("\"", "\\\""); // into a quoted Overpass string
        // Area first, then the tag match: the server narrows to the box before running the regex.
        var ql = FormattableString.Invariant(
            $"[out:json][timeout:10];nwr({a.South},{a.West},{a.North},{a.East})->.a;nwr.a[name][~\"^(name|brand|cuisine|amenity|shop|tourism|leisure)$\"~\"{re}\",i];out center 40;");
        Osm? osm = null;
        foreach (var server in Overpass) // the main one is often at capacity; a mirror usually isn't
        {
            try
            {
                using var res = await server.PostAsync("interpreter", new FormUrlEncodedContent([new("data", ql)]));
                if (!res.IsSuccessStatusCode) continue; // 429/502/504: full
                osm = await res.Content.ReadFromJsonAsync<Osm>();
                break;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        if (osm is null) return null;
        return osm.Elements.Where(e => e.Tags is not null && (e.Lat ?? e.Center?.Lat) is not null).Select(e =>
        {
            var t = e.Tags!;
            var street = string.Join(" ", new[] { t.GetValueOrDefault("addr:street"), t.GetValueOrDefault("addr:housenumber") }.Where(s => s is not null));
            var address = string.Join(", ", new[] { street, t.GetValueOrDefault("addr:city") }.Where(s => !string.IsNullOrEmpty(s)));
            var p = new Pin { Name = t["name"], Lat = e.Lat ?? e.Center!.Lat, Lon = e.Lon ?? e.Center!.Lon, Color = "#F4F4F5" };
            var type = t.GetValueOrDefault("amenity") ?? t.GetValueOrDefault("shop") ?? t.GetValueOrDefault("tourism") ?? t.GetValueOrDefault("leisure");
            return Details(p, t, type, address.Length > 0 ? address : null);
        }).ToList();
    }

    static readonly Dictionary<char, string> Accents = new()
    {
        ['a'] = "[aá]", ['á'] = "[aá]", ['e'] = "[eé]", ['é'] = "[eé]", ['i'] = "[ií]", ['í'] = "[ií]",
        ['o'] = "[oóöő]", ['ó'] = "[oóöő]", ['ö'] = "[oóöő]", ['ő'] = "[oóöő]", ['u'] = "[uúüű]", ['ú'] = "[uúüű]", ['ü'] = "[uúüű]", ['ű'] = "[uúüű]",
    };

    /// Search text → regex: "pizza" → "p[ií]zz". One-word queries of 5+ letters lose their last letter (so "pizza" finds
    /// "Pizzéria"), Hungarian vowels match with or without accents, regex characters are literal.
    public static string Pattern(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length >= 5 && !q.Contains(' ')) q = q[..^1];
        return string.Concat(q.Select(c => Accents.TryGetValue(c, out var any) ? any : @".^$*+?()[]{}|\".Contains(c) ? @"\" + c : c.ToString()));
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
        return Details(p, h.Extratags ?? [], h.Type, Street(h.Address));
    }

    // From OSM tags, whichever service they came from.
    static Pin Details(Pin p, Dictionary<string, string> tags, string? type, string? address)
    {
        var kind = type is null or "yes" ? null : Cap(type.Replace('_', ' '));
        if (tags.GetValueOrDefault("cuisine") is { } cuisine) kind = $"{kind ?? "Food"} · {cuisine.Replace(";", ", ").Replace('_', ' ')}";
        p.Kind = kind;
        p.Address = address;
        p.Hours = tags.GetValueOrDefault("opening_hours");
        p.Phone = new[] { "phone", "contact:phone", "mobile", "contact:mobile" }.Select(tags.GetValueOrDefault).FirstOrDefault(v => v is not null);
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

    /// Why a lookup failed, in words that say whose end it is: the user's connection or OpenStreetMap's server.
    public static string Why(Exception e) => e switch
    {
        _ when !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() => "You're offline. Check your connection.",
        TaskCanceledException => "OpenStreetMap didn't answer in time (their server, not Glance). Try again in a minute.",
        HttpRequestException { StatusCode: (System.Net.HttpStatusCode)429 or >= System.Net.HttpStatusCode.InternalServerError } =>
            "OpenStreetMap is busy right now (their server, not Glance). Try again in a minute.",
        HttpRequestException { StatusCode: null } => "Can't reach OpenStreetMap. Check your connection, or their server may be down.",
        _ => "OpenStreetMap sent a reply Glance couldn't read. Try again later.",
    };
}
