using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace Glance.Services;

/// One income (Amount > 0) or expense (Amount < 0), in the currency money is kept in (Money.Home).
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

    /// The currency Budget, Bills and Exchange count in (Settings → Money). Changing it relabels; amounts keep their numbers.
    public static string Home => App.Settings.Currency;

    /// "+12 000 Ft", "−12.99 €", "$1,200" (unsigned when plus is false). Cents only when there are some, and never for
    /// currencies without them in everyday use (forint, yen). decimals: always that many (exchange rates).
    public static string Format(double amount, bool plus = true, string? currency = null, int? decimals = null)
    {
        var c = Rates.Of(currency ?? Home);
        var digits = decimals ?? (c.Whole || Math.Abs(Math.Round(amount, 2) % 1) < 0.005 ? 0 : 2);
        var n = Math.Abs(amount).ToString("N" + digits);
        var sign = !plus ? "" : amount < 0 ? "−" : amount > 0 ? "+" : "";
        return c.Before ? $"{sign}{c.Symbol}{n}" : $"{sign}{n} {c.Symbol}";
    }

    /// amount in `currency`, in the home currency; null while there's no rate for it.
    public static double? InHome(double amount, string currency) => Rates.Convert(amount, currency, Home);

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
    /// In another currency it's booked at today's rate, with what was paid in the note; false (nothing done) without a rate.
    public static bool Pay(Bill b)
    {
        if (InHome(b.Amount, b.Currency) is not { } amount) return false;
        Entries.Add(new Entry(DateTime.Today, -amount, PaidNote(b)));
        if (b.Repeat == Repeat.Once) Bills.Remove(b);
        else Replace(b, b with { Due = b.NextDue(), Reminded = null, Paid = b.Due });
        return true;
    }

    /// Undoes Pay: back to the due date it was paid for, and its expense leaves Budget (the latest one with its note).
    public static void Unpay(Bill b)
    {
        if (b.Paid is not { } was) return;
        var note = PaidNote(b);
        for (var i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Amount < 0 && Entries[i].Note == note) { Entries.RemoveAt(i); break; }
        Replace(b, b with { Due = was, Paid = null });
    }

    static string PaidNote(Bill b) => b.Currency == Home ? b.Name : $"{b.Name} · {b.AmountText}";

    /// Bills whose reminder is due (from 9:00 the day before) and hasn't shown for this due date yet.
    public static List<Bill> DueReminders(DateTime now) =>
        Bills.Where(b => b.Remind && b.Reminded != b.Due && now >= b.Due.Date.AddDays(-1).AddHours(9)).ToList();
}

public enum Repeat { Monthly, Quarterly, Yearly, Weekly, Once }

/// Something to pay, by a date, maybe again and again. Day: the day of the month it's due on, so a bill on the 31st
/// goes Jan 31 → Feb 28 → Mar 31 instead of drifting to the 28th. Reminded: the due date its reminder last showed for.
/// Paid: the due date it was last paid for (Due has moved on to the next one); null once edited to another date or unticked.
public sealed record Bill(string Name, double Amount, DateTime Due, Repeat Repeat, string Kind, bool Remind = true, DateTime? Reminded = null, int Day = 0, string Currency = "HUF",
    DateTime? Paid = null)
{
    /// Shows as paid until the next due date is close (three days, when Bills turns it amber).
    public bool PaidUp(DateTime today) => Paid is not null && (Due.Date - today.Date).Days > 3;

    /// "12.99 €": the amount in its own currency (Currency: what it's charged in; bills from before there was a choice were forints).
    public string AmountText => Money.Format(Amount, plus: false, currency: Currency);

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
