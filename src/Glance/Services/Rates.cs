using System.Net.Http;
using System.Net.Http.Json;

namespace Glance.Services;

// ECB reference rates via Frankfurter: free, no key, updated once per working day.
public static class Rates
{
    public static readonly (string Code, string Name)[] Currencies = [("EUR", "Euro"), ("USD", "US dollar"), ("GBP", "British pound"), ("CHF", "Swiss franc")];

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    record Series(Dictionary<string, Dictionary<string, double>> Rates);

    /// Forint per 1 unit of each currency, oldest → newest.
    public static async Task<(string[] Dates, Dictionary<string, float[]> Huf)> Get(int days)
    {
        // Base EUR keeps ECB's full precision; other pairs are cross rates (HUF/EUR ÷ X/EUR).
        var from = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd");
        var symbols = string.Join(',', Currencies.Select(c => c.Code).Where(c => c != "EUR").Append("HUF"));
        var s = await http.GetFromJsonAsync<Series>($"https://api.frankfurter.dev/v1/{from}..?base=EUR&symbols={symbols}")
            ?? throw new HttpRequestException("empty response");
        var dates = s.Rates.Keys.Order().ToArray();
        return (dates, Currencies.ToDictionary(c => c.Code, c => dates.Select(d =>
        {
            var r = s.Rates[d];
            var perEur = c.Code == "EUR" ? 1 : r.GetValueOrDefault(c.Code);
            return r.TryGetValue("HUF", out var huf) && perEur > 0 ? (float)(huf / perEur) : float.NaN;
        }).ToArray()));
    }
}
