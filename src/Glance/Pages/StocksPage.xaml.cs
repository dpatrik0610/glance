using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// A watchlist of stock and index symbols (kept in settings) with price, today's change and three months of closes.
public partial class StocksPage : UserControl
{
    public record Card(string Symbol, string Name, string Price, string Change, Brush Brush, string Range, float[] Closes, string Format);

    static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    readonly Dictionary<string, Stocks.Quote> quotes = [];
    DateTime loadedAt;
    bool failed;

    static List<string> Symbols => App.Settings.StockSymbols;

    public StocksPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > MaxAge) Load(); };
        Render();
    }

    async void Load()
    {
        loadedAt = DateTime.Now;
        if (quotes.Count == 0 && Symbols.Count > 0) Status.Text = "Loading…";
        failed = false;
        await Task.WhenAll(Symbols.ToArray().Select(Fetch));
        if (failed) loadedAt = default; // try again on the next visit
        Render();
    }

    /// False when Yahoo doesn't know the symbol.
    async Task<bool> Fetch(string symbol)
    {
        try
        {
            if (await Stocks.Get(symbol) is not { } q) return false;
            quotes[symbol] = q;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            failed = true;
            Db.Error($"quote {symbol}", e);
        }
        return true;
    }

    void Render()
    {
        Cards.ItemsSource = Symbols.Where(quotes.ContainsKey).Select(s =>
        {
            var q = quotes[s];
            var traded = q.Closes.Where(v => !float.IsNaN(v)).DefaultIfEmpty().ToArray();
            return new Card(q.Symbol, q.Name, $"{q.Price:N2} {q.Currency}", $"{(q.Change >= 0 ? "▲" : "▼")} {Math.Abs(q.Change):0.00}% today",
                Skin.Brush(q.Change >= 0 ? "Green" : "Red"), $"3 months · low {traded.Min():N2} · high {traded.Max():N2}", q.Closes, $"{{0:N2}} {q.Currency}");
        }).ToArray();
        Status.Text = Symbols.Count == 0 ? "Add a symbol to follow its price."
            : quotes.Count > 0 ? "" : failed ? "Can't reach Yahoo Finance." : Status.Text; // still loading
    }

    void SymbolKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Add(sender, e);
    }

    async void Add(object sender, RoutedEventArgs e)
    {
        var symbol = SymbolBox.Text.Trim().ToUpperInvariant();
        if (symbol.Length == 0) return;
        if (Symbols.Contains(symbol)) { Say($"{symbol} is already on the list."); return; }
        Say(null);
        failed = false;
        if (!await Fetch(symbol)) { Say($"Yahoo Finance doesn't know {symbol}."); return; }
        if (failed) { Say("Can't reach Yahoo Finance, try again in a moment."); return; }
        Symbols.Add(symbol);
        App.Settings.Save();
        SymbolBox.Clear();
        Render();
    }

    void RemoveCard(object sender, RoutedEventArgs e)
    {
        var symbol = ((Card)((FrameworkElement)sender).DataContext).Symbol;
        Symbols.RemoveAll(s => quotes.TryGetValue(s, out var q) && q.Symbol == symbol);
        App.Settings.Save();
        Render();
    }

    void Say(string? text)
    {
        Hint.Text = text ?? "";
        Hint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
