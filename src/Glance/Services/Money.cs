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
        var list = Settings.ReadJson<ObservableCollection<Entry>>(File_) ?? [];
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

    static readonly string BillsFile = Path.Combine(Settings.Dir, "bills.json");

    /// Things that come back (rent, subscriptions, insurance…), in bills.json. Saved on every change, like Entries.
    public static ObservableCollection<Bill> Bills { get; } = LoadBills();

    static ObservableCollection<Bill> LoadBills()
    {
        var list = Settings.ReadJson<ObservableCollection<Bill>>(BillsFile) ?? [];
        list.CollectionChanged += (_, _) => SaveBills();
        Application.Current.Exit += (_, _) => SaveBills();
        return list;
    }

    static void SaveBills()
    {
        try { Settings.WriteAtomic(BillsFile, JsonSerializer.Serialize(Bills)); }
        catch (IOException e) { Db.Error("bills.json save", e); }
    }

    /// Bills are records: a change swaps in the new one, where the old one was.
    public static void Replace(Bill old, Bill now)
    {
        var i = Bills.IndexOf(old);
        if (i >= 0) Bills[i] = now; else Bills.Add(now);
    }

    /// Paid today: an expense in Budget, and the bill moves to its next due date (a one-off is done and goes).
    public static void Pay(Bill b)
    {
        Entries.Add(new Entry(DateTime.Today, -b.Amount, b.Name));
        if (b.Repeat == Repeat.Once) Bills.Remove(b);
        else Replace(b, b with { Due = b.NextDue(), Reminded = null });
    }

    /// Bills whose reminder is due (from 9:00 the day before) and hasn't shown for this due date yet.
    public static List<Bill> DueReminders(DateTime now) =>
        Bills.Where(b => b.Remind && b.Reminded != b.Due && now >= b.Due.Date.AddDays(-1).AddHours(9)).ToList();
}

public enum Repeat { Monthly, Quarterly, Yearly, Weekly, Once }

/// Something to pay, by a date, maybe again and again. Day: the day of the month it's due on, so a bill on the 31st
/// goes Jan 31 → Feb 28 → Mar 31 instead of drifting to the 28th. Reminded: the due date its reminder last showed for.
public sealed record Bill(string Name, double Amount, DateTime Due, Repeat Repeat, string Kind, bool Remind = true, DateTime? Reminded = null, int Day = 0)
{
    public static readonly string[] Kinds = ["Subscription", "Housing", "Utilities", "Insurance", "Loan", "Other"];

    public DateTime NextDue()
    {
        if (Repeat == Repeat.Weekly) return Due.AddDays(7);
        var d = Due.AddMonths(Repeat switch { Repeat.Quarterly => 3, Repeat.Yearly => 12, _ => 1 });
        var day = Day > 0 ? Day : Due.Day;
        return new DateTime(d.Year, d.Month, Math.Min(day, DateTime.DaysInMonth(d.Year, d.Month)));
    }

    /// What it costs a month, on average (a one-off: nothing).
    public double PerMonth => Repeat switch
    {
        Repeat.Weekly => Amount * 52 / 12,
        Repeat.Monthly => Amount,
        Repeat.Quarterly => Amount / 3,
        Repeat.Yearly => Amount / 12,
        _ => 0,
    };

    /// "Today", "Tomorrow", "in 4 days", "Dec 5", "3 days late".
    public string When(DateTime today) => (Due.Date - today.Date).Days switch
    {
        < -1 and var d => $"{-d} days late",
        -1 => "1 day late",
        0 => "Today",
        1 => "Tomorrow",
        < 7 and var d => $"in {d} days",
        _ => Due.ToString(Due.Year == today.Year ? "MMM d" : "yyyy MMM d", System.Globalization.CultureInfo.InvariantCulture),
    };
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
