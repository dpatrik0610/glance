using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

public partial class WeatherPage : UserControl
{
    record HourBar(string Hour, string Temp, string Rain, GridLength Space, GridLength Fill, Brush Brush);
    record DayRow(string Day, string Glyph, string Rain, string Min, string Max, GridLength Before, GridLength Bar, GridLength After);

    static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);
    DateTime fetchedAt;
    (double?, double?, bool) fetchedFor;

    static (double?, double?, bool) Key() => (App.Settings.Lat, App.Settings.Lon, App.Settings.Fahrenheit);

    public WeatherPage()
    {
        InitializeComponent();
        // Only fetch when looked at and stale: no background polling.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - fetchedAt > MaxAge) _ = Load(); };
        App.SettingsChanged += () => { if (Key() != fetchedFor) fetchedAt = default; };
    }

    async Task Load()
    {
        var s = App.Settings;
        if (s.Lat is not { } lat || s.Lon is not { } lon)
        {
            Show("Set your city in Settings.");
            return;
        }
        fetchedAt = DateTime.Now;
        fetchedFor = Key();
        if (Body.Visibility != Visibility.Visible) Show("Loading…");
        try
        {
            var f = await Weather.Get(lat, lon, s.Fahrenheit) ?? throw new HttpRequestException("empty response");
            Render(f, s);
            Status.Text = "";
            Body.Visibility = Visibility.Visible;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            fetchedAt = default; // retry next time the page is shown
            Db.Error("weather", e);
            if (Body.Visibility != Visibility.Visible) Show("Weather unavailable.");
        }
    }

    void Show(string msg)
    {
        Body.Visibility = Visibility.Collapsed;
        Status.Text = msg;
    }

    void Render(Weather.Forecast f, Settings s)
    {
        var c = f.Current;
        var (glyph, text) = Weather.Describe(c.Code, c.IsDay == 1);
        Place.Text = s.City;
        NowGlyph.Text = glyph;
        NowTemp.Text = $"{c.Temp:0}°";
        NowText.Text = text;
        HiLo.Text = $"H {f.Daily.Max[0]:0}°  L {f.Daily.Min[0]:0}°";
        Feels.Text = $"{c.Feels:0}°";
        Humidity.Text = $"{c.Humidity}%";
        Wind.Text = $"{c.Wind:0} {(s.Fahrenheit ? "mph" : "km/h")}";
        Uv.Text = f.Daily.Uv[0] is { } uv ? $"{uv:0}" : "—";
        Sunrise.Text = Clock(f.Daily.Sunrise[0]);
        Sunset.Text = Clock(f.Daily.Sunset[0]);

        // Times are local to the city (timezone=auto), so match against the API's "now", not the PC clock.
        var h = f.Hourly;
        var nowHour = c.Time[..13];
        var start = Math.Max(0, Array.FindIndex(h.Time, t => t.StartsWith(nowHour)));
        var n = Math.Min(24, h.Time.Length - start);
        var temps = h.Temp.AsSpan(start, n);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var t in temps) { lo = Math.Min(lo, t); hi = Math.Max(hi, t); }
        var range = Math.Max(1, hi - lo);
        var accent = (Brush)FindResource("Accent");
        var dim = (Brush)FindResource("Track");
        Hours.ItemsSource = Enumerable.Range(start, n).Select(i =>
        {
            var share = 0.25 + 0.75 * (h.Temp[i] - lo) / range; // keep the coldest bar visible
            return new HourBar(i == start ? "Now" : h.Time[i][11..13], $"{h.Temp[i]:0}°",
                h.Rain[i] is > 0 and var r ? $"{r}%" : "",
                new GridLength(1 - share, GridUnitType.Star), new GridLength(share, GridUnitType.Star),
                i == start ? accent : dim);
        }).ToArray();

        // Range bars share one scale across the week.
        var d = f.Daily;
        double wlo = d.Min.Min(), whi = d.Max.Max();
        Days.ItemsSource = Enumerable.Range(0, d.Time.Length).Select(i => new DayRow(
            i == 0 ? "Today" : DateTime.ParseExact(d.Time[i], "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("ddd"),
            Weather.Describe(d.Code[i]).Glyph,
            d.Rain[i] is > 0 and var r ? $"{r}%" : "",
            $"{d.Min[i]:0}°", $"{d.Max[i]:0}°",
            Star(d.Min[i] - wlo), Star(d.Max[i] - d.Min[i]), Star(whi - d.Max[i]))).ToArray();
    }

    static GridLength Star(double v) => new(Math.Max(0, v), GridUnitType.Star);

    static string Clock(string iso) => iso.Length >= 16 ? iso[11..16] : iso;
}
