using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Glance.Services;

/// A currency as Glance writes it: "4 500 Ft", "12.99 €", "$12.99". Whole: no minor unit in everyday use (forint, yen).
public sealed record Currency(string Code, string Name, string Symbol, bool Before, bool Whole);

// ECB reference rates via Frankfurter: free, no key, updated once per working day.
public static class Rates
{
    /// The currencies ECB publishes rates for, so every one of them converts.
    public static readonly Currency[] All =
    [
        new("HUF", "Hungarian forint", "Ft", false, true), new("EUR", "Euro", "€", false, false), new("USD", "US dollar", "$", true, false),
        new("GBP", "British pound", "£", true, false), new("CHF", "Swiss franc", "CHF", false, false), new("PLN", "Polish złoty", "zł", false, false),
        new("CZK", "Czech koruna", "Kč", false, false), new("RON", "Romanian leu", "lei", false, false), new("SEK", "Swedish krona", "kr", false, false),
        new("NOK", "Norwegian krone", "kr", false, false), new("DKK", "Danish krone", "kr", false, false), new("ISK", "Icelandic króna", "kr", false, true),
        new("TRY", "Turkish lira", "₺", false, false), new("JPY", "Japanese yen", "¥", true, true), new("CNY", "Chinese yuan", "CN¥", true, false),
        new("KRW", "South Korean won", "₩", true, true), new("INR", "Indian rupee", "₹", true, false), new("CAD", "Canadian dollar", "CA$", true, false),
        new("AUD", "Australian dollar", "A$", true, false), new("NZD", "New Zealand dollar", "NZ$", true, false), new("HKD", "Hong Kong dollar", "HK$", true, false),
        new("SGD", "Singapore dollar", "S$", true, false), new("MXN", "Mexican peso", "MX$", true, false), new("BRL", "Brazilian real", "R$", true, false),
        new("ZAR", "South African rand", "R", true, false), new("ILS", "Israeli shekel", "₪", true, false), new("THB", "Thai baht", "฿", true, false),
        new("IDR", "Indonesian rupiah", "Rp", true, true), new("MYR", "Malaysian ringgit", "RM", true, false), new("PHP", "Philippine peso", "₱", true, false),
    ];

    public static Currency Of(string code) => All.FirstOrDefault(c => c.Code == code) ?? new(code, code, code, false, false);

    /// The Exchange page's cards: the usual ones, without the one money is kept in.
    public static string[] Watched(string home) => new[] { "EUR", "USD", "GBP", "CHF", "HUF" }.Where(c => c != home).Take(4).ToArray();

    /// Enough decimals to see a rate move: 390.12 Ft, 0.9312 €, 0.002551 € for a forint.
    public static int Digits(double v) => v >= 10 ? 2 : v >= 0.1 ? 4 : 6;

    /// A rate in `home`, as text ("390.12 Ft") and as a sparkline's hover format.
    public static string Text(double v, string home) => Money.Format(v, plus: false, currency: home, decimals: Digits(v));
    public static string SparkFormat(double v, string home)
    {
        var c = Of(home);
        var n = $"{{0:N{Digits(v)}}}";
        return c.Before ? c.Symbol + n : $"{n} {c.Symbol}";
    }

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    record Series(Dictionary<string, Dictionary<string, double>> Rates);
    record Latest(string Date, Dictionary<string, double> Rates);

    /// How much `home` one unit of each code costs, oldest → newest.
    public static async Task<(string[] Dates, Dictionary<string, float[]> InHome)> Get(int days, string home, string[] codes)
    {
        // Base EUR keeps ECB's full precision; other pairs are cross rates (home/EUR ÷ X/EUR).
        var from = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd");
        var symbols = string.Join(',', codes.Append(home).Where(c => c != "EUR").Distinct());
        var s = await http.GetFromJsonAsync<Series>($"https://api.frankfurter.dev/v1/{from}..?base=EUR&symbols={symbols}")
            ?? throw new HttpRequestException("empty response");
        var dates = s.Rates.Keys.Order().ToArray();
        double PerEur(Dictionary<string, double> r, string c) => c == "EUR" ? 1 : r.GetValueOrDefault(c);
        return (dates, codes.ToDictionary(c => c, c => dates.Select(d =>
        {
            var r = s.Rates[d];
            var (h, x) = (PerEur(r, home), PerEur(r, c));
            return h > 0 && x > 0 ? (float)(h / x) : float.NaN;
        }).ToArray()));
    }

    // ---- Today's rates, for bills in another currency. Kept in rates.json, so they convert offline and right at startup. ----

    static readonly string File_ = Path.Combine(Settings.Dir, "rates.json");
    static Latest? latest = Settings.ReadJson<Latest>(File_);
    static DateTime triedAt;

    /// The date of the rates in use ("" before the first fetch).
    public static string Date => latest?.Date ?? "";

    /// amount in `from`, in `to`; null while there's no rate for one of them.
    public static double? Convert(double amount, string from, string to)
    {
        if (from == to) return amount;
        double? PerEur(string c) => c == "EUR" ? 1 : latest?.Rates.TryGetValue(c, out var r) == true && r > 0 ? r : null;
        return PerEur(from) is { } f && PerEur(to) is { } t ? amount / f * t : null;
    }

    /// Fetches today's rates when the ones kept are from an earlier day (at most every 3 hours). True when they changed.
    public static async Task<bool> Refresh()
    {
        if (latest?.Date == DateTime.Today.ToString("yyyy-MM-dd") || DateTime.Now - triedAt < TimeSpan.FromHours(3)) return false;
        triedAt = DateTime.Now;
        try
        {
            var l = await http.GetFromJsonAsync<Latest>("https://api.frankfurter.dev/v1/latest?base=EUR");
            if (l is null || l.Date == latest?.Date) return false;
            latest = l;
            Settings.WriteAtomic(File_, JsonSerializer.Serialize(l));
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Db.Warn($"rates: {e.Message}"); // keep the last ones
            return false;
        }
    }

    /// The selftest's fixed rates.
    internal static void Use(string date, Dictionary<string, double> perEur) => latest = new(date, perEur);
}
