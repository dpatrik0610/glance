using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// A Worker or Pages project: its newest deployment, 24h traffic (Workers), and its history once clicked.
public sealed class AppRow(Deploy deploy, string kind, string traffic, ObservableCollection<Deploy> history)
{
    public Deploy Deploy => deploy;
    public string Kind => kind;
    public string Traffic => traffic;
    public bool HasUrl => deploy.Url.Length > 0;
    public ObservableCollection<Deploy> History => history;
}

public partial class CloudflarePage : UserControl
{
    readonly DispatcherTimer timer;
    // Opened histories survive the minutely refresh (rows are rebuilt, their history lists are kept by key).
    readonly Dictionary<string, ObservableCollection<Deploy>> histories = [];
    bool busy;

    public CloudflarePage()
    {
        InitializeComponent();
        // Polls only while visible; the deploy alerts poll on their own (App.CheckCloudflare).
        timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Load(), Dispatcher);
        IsVisibleChanged += (_, e) =>
        {
            timer.IsEnabled = (bool)e.NewValue;
            if (timer.IsEnabled) Load();
        };
    }

    /// Runs one fetch; a failure (missing permission, offline) becomes the section's note instead of breaking the page.
    static async Task<(T? Value, string Error)> Try<T>(Task<T> task)
    {
        try { return (await task, ""); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Db.Warn($"cloudflare: {e.Message}");
            return (default, e is TaskCanceledException ? "Cloudflare didn't answer" : e.Message);
        }
    }

    async void Load()
    {
        var connected = Cloudflare.Connected;
        Body.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (!connected || busy) return;
        busy = true;
        try
        {
            var (workers, pages, traffic, zones, r2) = (Try(Cloudflare.Workers()), Try(Cloudflare.Pages()), Try(Cloudflare.Traffic()),
                Try(Cloudflare.Zones()), Try(Cloudflare.R2Sizes()));
            await Task.WhenAll(workers, pages, traffic, zones, r2);
            Account.Text = Cloudflare.AccountName;

            var use = traffic.Result.Value ?? [];
            string Traffic(Deploy d) => use.TryGetValue(d.Name, out var u) ? $"{u.Requests:N0} req · {u.Errors:N0} err · {u.CpuMs:0.#} ms" : "";
            AppRow Row(Deploy d, string kind, string t) =>
                new(d, kind, t, histories.TryGetValue(d.Key, out var h) ? h : (histories[d.Key] = []));
            Apps.ItemsSource = (workers.Result.Value ?? []).Select(d => Row(d, "Worker", Traffic(d)))
                .Concat((pages.Result.Value ?? []).Select(d => Row(d, "Pages", ""))).ToList();
            AppsNote.Text = string.Join("\n", new[] { Why("Workers", workers.Result.Error), Why("Pages", pages.Result.Error), Why("Traffic", traffic.Result.Error) }
                .Where(s => s.Length > 0));
            if (AppsNote.Text == "" && Apps.Items.Count == 0) AppsNote.Text = "No Workers or Pages projects yet";

            Zones.ItemsSource = zones.Result.Value;
            ZonesNote.Text = Why("Domains", zones.Result.Error) is { Length: > 0 } z ? z : zones.Result.Value?.Count == 0 ? "No domains" : "";

            var (stores, storeError) = await Try(Cloudflare.Storage(r2.Result.Value ?? new Dictionary<string, long>()));
            Stores.ItemsSource = stores;
            StorageNote.Text = Why("Storage", storeError) is { Length: > 0 } s ? s : stores?.Count == 0 ? "No D1, KV or R2 storage" : "";
        }
        finally { busy = false; }
    }

    static string Why(string what, string error) => error.Length == 0 ? "" : $"{what}: {error}";

    void Refresh(object sender, RoutedEventArgs e) => Load();

    async void RowClick(object sender, MouseButtonEventArgs e)
    {
        var row = (AppRow)((FrameworkElement)sender).DataContext;
        if (row.History.Count > 0) { row.History.Clear(); return; }
        var (list, error) = await Try(Cloudflare.History(row.Deploy.Key));
        if (list is null) { Dialog.Info("Cloudflare", $"Couldn't load {row.Deploy.Name}'s deployments", error); return; }
        foreach (var d in list) row.History.Add(d);
    }

    void OpenUrl(object sender, RoutedEventArgs e) => Shell.OpenLink(((AppRow)((FrameworkElement)sender).DataContext).Deploy.Url);

    void TokenPage(object sender, RoutedEventArgs e) => Shell.OpenLink(Cloudflare.TokenPage);

    async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (TokenBox.Text.Trim().Length == 0) { ConnectStatus.Text = "Paste a token first."; return; }
        ConnectButton.IsEnabled = false;
        ConnectStatus.Text = "Checking the token…";
        var (_, error) = await Try(Cloudflare.Connect(TokenBox.Text));
        ConnectButton.IsEnabled = true;
        ConnectStatus.Text = error;
        if (error.Length > 0) return;
        TokenBox.Text = "";
        Load();
    }

    void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm("Cloudflare", "Disconnect Cloudflare?", "Glance forgets the token. Deploy alerts stop until you connect again.", "Disconnect")) return;
        Cloudflare.Disconnect();
        histories.Clear();
        Load();
    }
}
