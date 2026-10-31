using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Glance.Services;

// Open-Meteo: free, no API key.
public static class Weather
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public record Place(string Name, string? Country, double Latitude, double Longitude);
    record Geo(Place[]? Results);

    public record Current(
        string Time,
        [property: JsonPropertyName("temperature_2m")] double Temp,
        [property: JsonPropertyName("apparent_temperature")] double Feels,
        [property: JsonPropertyName("relative_humidity_2m")] int Humidity,
        [property: JsonPropertyName("wind_speed_10m")] double Wind,
        [property: JsonPropertyName("weather_code")] int Code,
        [property: JsonPropertyName("is_day")] int IsDay);

    public record Hourly(
        string[] Time,
        [property: JsonPropertyName("temperature_2m")] double[] Temp,
        [property: JsonPropertyName("precipitation_probability")] int?[] Rain,
        [property: JsonPropertyName("weather_code")] int[] Code);

    public record Daily(
        string[] Time,
        [property: JsonPropertyName("weather_code")] int[] Code,
        [property: JsonPropertyName("temperature_2m_max")] double[] Max,
        [property: JsonPropertyName("temperature_2m_min")] double[] Min,
        [property: JsonPropertyName("precipitation_probability_max")] int?[] Rain,
        [property: JsonPropertyName("uv_index_max")] double?[] Uv,
        string[] Sunrise, string[] Sunset);

    public record Forecast(Current Current, Hourly Hourly, Daily Daily);

    public static async Task<Place?> Find(string city) =>
        (await http.GetFromJsonAsync<Geo>(
            $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}"))?.Results?.FirstOrDefault();

    public static Task<Forecast?> Get(double lat, double lon, bool fahrenheit) => http.GetFromJsonAsync<Forecast>(
        "https://api.open-meteo.com/v1/forecast?timezone=auto&forecast_days=7" +
        $"&latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
        "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day" +
        "&hourly=temperature_2m,precipitation_probability,weather_code" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,uv_index_max,sunrise,sunset" +
        (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : ""));

    // WMO weather codes.
    public static (string Glyph, string Text) Describe(int code, bool day = true) => code switch
    {
        0 => (day ? "☀" : "☾", "Clear"),
        1 or 2 => (day ? "⛅" : "☁", code == 1 ? "Mostly clear" : "Partly cloudy"),
        3 => ("☁", "Overcast"),
        45 or 48 => ("≋", "Fog"),
        >= 51 and <= 57 => ("☂", "Drizzle"),
        >= 61 and <= 67 => ("☂", "Rain"),
        >= 71 and <= 77 => ("❄", "Snow"),
        >= 80 and <= 82 => ("☂", "Showers"),
        85 or 86 => ("❄", "Snow showers"),
        >= 95 => ("⚡", "Thunderstorm"),
        _ => ("·", "—"),
    };
}
