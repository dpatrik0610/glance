using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

/// What the usual currencies cost in the one money is kept in (Settings → Money), and a converter.
public partial class ExchangePage : UserControl
{
    public record Card(string Code, string Name, string Value, string Change, string Range, float[] Series, string SparkFormat);

    const int Days = 90;
    Dictionary<string, float[]> inHome = [];
    string home = "", currency = "";
    bool toHome = true;
    DateTime loadedAt;

    public ExchangePage()
    {
        InitializeComponent();
        // ECB publishes once per working day; an hourly refetch is plenty. Another home currency: start over.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            if (home != Money.Home) Reset();
            if (DateTime.Now - loadedAt > TimeSpan.FromHours(1)) Load();
        };
    }

    void Reset()
    {
        home = Money.Home;
        inHome = [];
        Cards.ItemsSource = null;
        loadedAt = default;
        CurrencyChips.Children.Clear();
        var watched = Rates.Watched(home);
        currency = watched[0];
        foreach (var code in watched)
        {
            var chip = new RadioButton { Content = code, Style = Skin.Style("Chip"), GroupName = "cur", IsChecked = code == currency };
            chip.Checked += (_, _) => { currency = code; Recalc(); };
            CurrencyChips.Children.Add(chip);
        }
        Recalc();
    }

    async void Load()
    {
        loadedAt = DateTime.Now;
        var asked = home;
        if (inHome.Count == 0) Status.Text = "Loading…";
        try
        {
            var watched = Rates.Watched(home);
            var (dates, series) = await Rates.Get(Days, home, watched);
            if (asked != home) return; // the currency changed meanwhile
            inHome = series;
            Status.Text = "";
            Source.Text = $"ECB reference rates · {dates.LastOrDefault()}";
            Cards.ItemsSource = watched.Select(code =>
            {
                var s = series[code];
                var last = s.LastOrDefault(v => !float.IsNaN(v));
                var monthAgo = s.Length > 21 ? s[^22] : s.FirstOrDefault(); // ~21 working days
                var pct = monthAgo > 0 ? (last - monthAgo) / monthAgo * 100 : 0;
                var valid = s.Where(v => !float.IsNaN(v)).DefaultIfEmpty().ToArray();
                var d = Rates.Digits(last);
                return new Card(code, Rates.Of(code).Name, Rates.Text(last, home), $"{(pct >= 0 ? "▲" : "▼")} {Math.Abs(pct):0.0}% 30d",
                    $"{Days} days · low {valid.Min().ToString("N" + d)} · high {valid.Max().ToString("N" + d)}", s, Rates.SparkFormat(last, home));
            }).ToArray();
            Recalc();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            loadedAt = default;
            Db.Error("rates", e);
            if (inHome.Count == 0) Status.Text = "Rates unavailable.";
        }
    }

    void Recalc(object? sender = null, TextChangedEventArgs? e = null)
    {
        if (Result is null) return; // TextChanged fires during InitializeComponent
        FromCode.Text = toHome ? currency : home;
        var rate = inHome.TryGetValue(currency, out var s) ? s.LastOrDefault(v => !float.IsNaN(v)) : 0;
        if (rate <= 0 || !TryParse(Amount.Text, out var amount))
        {
            Result.Text = "";
            return;
        }
        Result.Text = "= " + (toHome ? Money.Format(amount * rate, plus: false, decimals: Rates.Of(home).Whole ? 0 : 2)
            : Money.Format(amount / rate, plus: false, currency: currency, decimals: 2));
    }

    void Swap(object sender, RoutedEventArgs e)
    {
        toHome = !toHome;
        Recalc();
    }

    // Accept both "1 234,5" (hu) and "1234.5".
    internal static bool TryParse(string text, out double v) =>
        double.TryParse(text.Replace(" ", "").Replace(" ", ""), NumberStyles.Number, CultureInfo.CurrentCulture, out v)
        || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out v);
}
