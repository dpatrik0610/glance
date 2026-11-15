using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

public partial class ExchangePage : UserControl
{
    public record Card(string Code, string Name, string Value, string Change, string Range, float[] Series);

    const int Days = 90;
    Dictionary<string, float[]> huf = [];
    string currency = "EUR";
    bool toHuf = true;
    DateTime loadedAt;

    public ExchangePage()
    {
        InitializeComponent();
        foreach (var (code, _) in Rates.Currencies)
        {
            var chip = new RadioButton { Content = code, Style = (Style)FindResource("Chip"), GroupName = "cur", IsChecked = code == currency };
            chip.Checked += (_, _) => { currency = code; Recalc(); };
            CurrencyChips.Children.Add(chip);
        }
        // ECB publishes once per working day; an hourly refetch is plenty.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > TimeSpan.FromHours(1)) Load(); };
    }

    async void Load()
    {
        loadedAt = DateTime.Now;
        if (huf.Count == 0) Status.Text = "Loading…";
        try
        {
            var (dates, series) = await Rates.Get(Days);
            huf = series;
            Status.Text = "";
            Source.Text = $"ECB reference rates · {dates.LastOrDefault()}";
            Cards.ItemsSource = Rates.Currencies.Select(c =>
            {
                var s = series[c.Code];
                var last = s.LastOrDefault(v => !float.IsNaN(v));
                var monthAgo = s.Length > 21 ? s[^22] : s.FirstOrDefault(); // ~21 working days
                var pct = monthAgo > 0 ? (last - monthAgo) / monthAgo * 100 : 0;
                var valid = s.Where(v => !float.IsNaN(v)).DefaultIfEmpty().ToArray();
                return new Card(c.Code, c.Name, $"{last:N2} Ft", $"{(pct >= 0 ? "▲" : "▼")} {Math.Abs(pct):0.0}% 30d",
                    $"{Days} days · low {valid.Min():N2} · high {valid.Max():N2}", s);
            }).ToArray();
            Recalc();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            loadedAt = default;
            Db.Error("rates", e);
            if (huf.Count == 0) Status.Text = "Rates unavailable.";
        }
    }

    void Recalc(object? sender = null, TextChangedEventArgs? e = null)
    {
        if (Result is null) return; // TextChanged fires during InitializeComponent
        FromCode.Text = toHuf ? currency : "HUF";
        var rate = huf.TryGetValue(currency, out var s) ? s.LastOrDefault(v => !float.IsNaN(v)) : 0;
        if (rate <= 0 || !TryParse(Amount.Text, out var amount))
        {
            Result.Text = "";
            return;
        }
        Result.Text = toHuf ? $"= {amount * rate:N0} Ft" : $"= {amount / rate:N2} {currency}";
    }

    void Swap(object sender, RoutedEventArgs e)
    {
        toHuf = !toHuf;
        Recalc();
    }

    // Accept both "1 234,5" (hu) and "1234.5".
    internal static bool TryParse(string text, out double v) =>
        double.TryParse(text.Replace(" ", "").Replace(" ", ""), NumberStyles.Number, CultureInfo.CurrentCulture, out v)
        || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out v);
}
