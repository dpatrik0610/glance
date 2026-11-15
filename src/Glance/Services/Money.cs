using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace Glance.Services;

/// One income (Amount > 0) or expense (Amount < 0), in forints.
public sealed record Entry(DateTime Date, double Amount, string Note);

/// Income and expenses, in money.json. Entries come one at a time, by hand, so every change saves right away (and exit
/// flushes, in case a save failed). A file that can't be read is set aside, never overwritten.
public static class Money
{
    static readonly string File_ = Path.Combine(Settings.Dir, "money.json");

    public static ObservableCollection<Entry> Entries { get; } = Load();

    static ObservableCollection<Entry> Load()
    {
        ObservableCollection<Entry> list;
        try { list = JsonSerializer.Deserialize<ObservableCollection<Entry>>(File.ReadAllText(File_)) ?? []; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { list = []; }
        catch (JsonException e)
        {
            var parked = $"{File_}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(File_, parked);
            Db.Error($"money.json unreadable, moved to {parked}", e);
            list = [];
        }
        list.CollectionChanged += (_, _) => Save();
        Application.Current.Exit += (_, _) => Save();
        return list;
    }

    static void Save()
    {
        try { Settings.WriteAtomic(File_, JsonSerializer.Serialize(Entries)); }
        catch (IOException e) { Db.Error("money.json save", e); } // still in memory: the next change or exit retries
    }

    /// "+12 000 Ft" / "−4 500 Ft" (unsigned when plus is false).
    public static string Format(double amount, bool plus = true) =>
        $"{(!plus ? "" : amount < 0 ? "−" : amount > 0 ? "+" : "")}{Math.Abs(amount):N0} Ft";
}

/// Delayed quotes from Yahoo Finance's chart endpoint: free, no key.
/// ponytail: unofficial API, can change or rate-limit without notice; move to a keyed provider if it starts failing.
public static class Stocks
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10), DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } } };

    /// Change: percent since the previous close. Closes: daily, the last three months, NaN = no trade.
    public record Quote(string Symbol, string Name, string Currency, double Price, double Change, float[] Closes);

    /// Null when Yahoo doesn't know the symbol.
    public static async Task<Quote?> Get(string symbol)
    {
        string json;
        try { json = await http.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=3mo&interval=1d"); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
        return Parse(symbol, json);
    }

    public static Quote? Parse(string symbol, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("chart").GetProperty("result") is not { ValueKind: JsonValueKind.Array } results || results.GetArrayLength() == 0)
            return null;
        var r = results[0];
        var meta = r.GetProperty("meta");
        var closes = r.TryGetProperty("indicators", out var ind) && ind.GetProperty("quote")[0].TryGetProperty("close", out var c)
            ? c.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : float.NaN).ToArray()
            : [];
        var price = meta.GetProperty("regularMarketPrice").GetDouble();
        var traded = closes.Where(v => !float.IsNaN(v)).ToArray();
        var prev = traded.Length > 1 ? traded[^2] : price;
        string? Text(string key) => meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new(Text("symbol") ?? symbol.ToUpperInvariant(), Text("shortName") ?? Text("longName") ?? symbol.ToUpperInvariant(),
            Text("currency") ?? "", price, prev > 0 ? (price - prev) / prev * 100 : 0, closes);
    }
}
