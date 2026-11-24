using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// A deployment as the page shows it: its status colors, and whether it has a link.
public class DeployView(Deploy deploy)
{
    static Brush Theme(string key) => Skin.Brush(key);
    static SolidColorBrush Tint(Brush b, byte alpha = 0x26) =>
        new(((SolidColorBrush)b).Color is var c ? Color.FromArgb(alpha, c.R, c.G, c.B) : default);

    public Deploy Deploy => deploy;
    public Brush StatusBrush => deploy.Id.Length == 0 ? Theme("Sub") : deploy.Status switch
    {
        "success" => Theme("Green"), "failure" => Theme("Red"), "active" or "idle" => Theme("Amber"), _ => Theme("Sub"),
    };
    public Brush StatusBg => Tint(StatusBrush);
    public bool HasUrl => deploy.Url.Length > 0;
    /// The oldest in a timeline: no rail below its dot.
    public bool Last { get; init; }

    internal static Brush Paint(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    internal static Brush PaintTint(string hex) => Tint(Paint(hex));
}

/// A Worker or Pages project: its newest deployment and, for Workers, the last 24 hours of traffic.
public sealed class AppRow(Deploy deploy, Usage? use) : DeployView(deploy)
{
    const string WorkerColor = "#F38020", PagesColor = "#5AA9FF";

    public bool IsWorker => Deploy.Key.StartsWith("worker:");
    public string Kind => IsWorker ? "Worker" : "Pages";
    public string Icon => IsWorker ? "" : ""; // lightning bolt, globe
    public Brush KindBrush => Paint(IsWorker ? WorkerColor : PagesColor);
    public Brush KindBg => PaintTint(IsWorker ? WorkerColor : PagesColor);
    public bool HasTraffic => use is not null;
    public IReadOnlyList<float>? Hourly => use?.Hourly;
    public string Requests => use is null ? "" : Cloudflare.Compact(use.Requests);
    public string Errors => use is null ? "" : use.Requests == 0 ? "0" : $"{Cloudflare.Compact(use.Errors)} · {use.Errors * 100.0 / use.Requests:0.##}%";
    public string Cpu => use is null ? "" : $"{use.CpuMs:0.#} ms";
    public string Dash => Cloudflare.Dash(IsWorker ? $"workers/services/view/{Deploy.Name}/production" : $"pages/view/{Deploy.Name}");
}

public sealed class ZoneView(Zone zone)
{
    public Zone Zone => zone;
    public string Status => zone.Status.Length == 0 ? "" : char.ToUpperInvariant(zone.Status[0]) + zone.Status[1..];
    public Brush StatusBrush => Skin.Brush(zone.Status switch { "active" => "Green", "pending" or "initializing" => "Amber", _ => "Sub" });
    public Brush StatusBg => new SolidColorBrush(((SolidColorBrush)StatusBrush).Color is var c ? Color.FromArgb(0x26, c.R, c.G, c.B) : default);
}

public sealed class StoreView(Store store)
{
    string Hex => store.Kind switch { "D1" => "#B07CFF", "KV" => "#3CC8C8", _ => "#E6A65D" };
    public Store Store => store;
    public Brush KindBrush => DeployView.Paint(Hex);
    public Brush KindBg => DeployView.PaintTint(Hex);
}

public partial class CloudflarePage : UserControl
{
    static readonly string[] Kinds = ["All", "Workers", "Pages"];

    readonly DispatcherTimer timer, toastTimer;
    List<AppRow> rows = [];
    ICollectionView? view;
    int kind;
    bool busy, loaded;
    int historyLoad; // the newest history fetch; older answers are dropped

    public CloudflarePage()
    {
        InitializeComponent();
        for (var i = 0; i < Kinds.Length; i++)
        {
            var index = i;
            var chip = new RadioButton { Content = Kinds[i], Style = Skin.Style("Chip"), GroupName = "cloudflareKind", IsChecked = i == 0 };
            chip.Checked += (_, _) => { kind = index; view?.Refresh(); Count(); };
            KindChips.Children.Add(chip);
        }
        toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.6), DispatcherPriority.Background, (_, _) =>
        {
            Toast.Visibility = Visibility.Collapsed;
            toastTimer!.Stop();
        }, Dispatcher) { IsEnabled = false };
        // Polls only while visible; the deploy alerts poll on their own (App.CheckCloudflare).
        timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Load(), Dispatcher);
        IsVisibleChanged += (_, e) =>
        {
            timer.IsEnabled = (bool)e.NewValue;
            if (timer.IsEnabled) Load();
        };
        PreviewKeyDown += Keys;
    }

    void Keys(object sender, KeyEventArgs e)
    {
        // AltGr arrives as Ctrl+Alt: typing, not a shortcut.
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (Body.Visibility != Visibility.Visible) return;
        if (e.Key == Key.F5) Load();
        else if (ctrl && e.Key == Key.F) { Search.Focus(); Search.SelectAll(); }
        else if (e.Key == Key.Escape && Search.IsKeyboardFocused && Search.Text.Length > 0) Search.Text = "";
        else if (e.Key == Key.Escape && Detail.Visibility == Visibility.Visible) Apps.SelectedItem = null;
        else return;
        e.Handled = true;
    }

    /// Runs one fetch; a failure (missing permission, offline) becomes the section's note instead of breaking the page.
    static async Task<(T? Value, string Error)> Try<T>(Task<T> task)
    {
        try { return (await task, ""); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Db.Warn($"cloudflare: {e.Message}");
            return (default, e is TaskCanceledException ? "Cloudflare didn't answer in time." : e.Message);
        }
    }

    async void Load()
    {
        var connected = Cloudflare.Connected;
        Body.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (!connected || busy) return;
        busy = true;
        RefreshButton.IsEnabled = false;
        Updated.Text = "Refreshing…";
        try
        {
            var (workersTask, pagesTask, trafficTask, zonesTask, r2Task) = (Try(Cloudflare.Workers(urls: true)), Try(Cloudflare.Pages()),
                Try(Cloudflare.Traffic()), Try(Cloudflare.Zones()), Try(Cloudflare.R2Sizes()));
            await Task.WhenAll(workersTask, pagesTask, trafficTask, zonesTask, r2Task);
            var (workers, pages, traffic, zones) = (workersTask.Result, pagesTask.Result, trafficTask.Result, zonesTask.Result);
            if (!Cloudflare.Connected) { Load(); return; } // disconnected meanwhile

            // Nothing came back at all: the token or the network, not a missing permission.
            var down = workers.Error.Length > 0 && pages.Error.Length > 0 && zones.Error.Length > 0;
            Banner.Visibility = down ? Visibility.Visible : Visibility.Collapsed;
            BannerText.Text = workers.Error;
            ReconnectButton.Visibility = down && !workers.Error.Contains("didn't answer") ? Visibility.Visible : Visibility.Collapsed;

            var use = traffic.Value ?? [];
            var selected = (Apps.SelectedItem as AppRow)?.Deploy.Key;
            rows = (workers.Value ?? []).Select(d => new AppRow(d, use.GetValueOrDefault(d.Name)))
                .Concat((pages.Value ?? []).Select(d => new AppRow(d, null)))
                .OrderByDescending(r => r.Deploy.When).ToList();
            view = CollectionViewSource.GetDefaultView(rows);
            view.Filter = Matches;
            Apps.ItemsSource = view;
            Apps.SelectedItem = rows.FirstOrDefault(r => r.Deploy.Key == selected); // keeps the details open across refreshes
            Count();
            AppsNote.Text = string.Join("\n", new[] { Why("Workers", workers.Error), Why("Pages", pages.Error) }.Where(s => s.Length > 0 && !down));
            Empty();

            var feed = rows.Where(r => r.Deploy.Id.Length > 0).OrderByDescending(r => r.Deploy.When).ToList();
            Feed.ItemsSource = feed;
            ActivityNote.Text = feed.Count == 0 && !down ? "No deployments yet." : "";

            Headline(workers.Value, pages.Value, zones.Value, down);
            Kpis(use, traffic.Error, feed.FirstOrDefault());

            Zones.ItemsSource = zones.Value?.Select(z => new ZoneView(z)).ToList();
            ZonesCount.Text = zones.Value is { Count: > 0 } zl ? $"{zl.Count(z => z.Status == "active")} of {zl.Count} active" : "";
            ZonesNote.Text = down ? "" : Why("Domains", zones.Error) is { Length: > 0 } z ? z : zones.Value?.Count == 0 ? "No domains on this account." : "";

            var (stores, storeError) = await Try(Cloudflare.Storage(r2Task.Result.Value ?? new Dictionary<string, long>()));
            Stores.ItemsSource = stores?.Select(s => new StoreView(s)).ToList();
            StoresCount.Text = stores is { Count: > 0 } ? $"{stores.Count(s => s.Kind == "D1")} D1 · {stores.Count(s => s.Kind == "KV")} KV · {stores.Count(s => s.Kind == "R2")} R2" : "";
            StorageNote.Text = down ? "" : Why("Storage", storeError) is { Length: > 0 } s ? s : stores?.Count == 0 ? "No D1, KV or R2 storage yet." : "";

            loaded = true;
            Updated.Text = $"Updated {DateTime.Now:HH:mm}";
        }
        finally
        {
            busy = false;
            RefreshButton.IsEnabled = true;
            if (!loaded) Updated.Text = "";
        }
    }

    static string Why(string what, string error) => error.Length == 0 ? "" : $"{what}: {error}";

    // ---- Header and KPIs ----

    void Headline(List<Deploy>? workers, List<Deploy>? pages, List<Zone>? zones, bool down)
    {
        var all = (workers ?? []).Concat(pages ?? []).ToList();
        var failed = all.Count(d => d.Failed);
        var building = all.Count(d => d.Building);
        var (title, color, icon) = down ? ("Can't reach Cloudflare", "Red", "")
            : failed > 0 ? (failed == 1 ? $"{all.First(d => d.Failed).Name}: build failed" : $"{failed} builds failed", "Red", "")
            : building > 0 ? (building == 1 ? $"{all.First(d => d.Building).Name} is building" : $"{building} builds running", "Amber", "")
            : all.Count == 0 ? ("Nothing deployed yet", "Sub", "")
            : ("All deployments live", "Green", "");
        Verdict.Text = title;
        BadgeIcon.Text = icon;
        BadgeIcon.Foreground = Skin.Brush(color);
        Badge.Background = color == "Sub" ? Skin.Brush("PillBg") : new SolidColorBrush(Skin.BrushColor(color) is var c ? Color.FromArgb(0x26, c.R, c.G, c.B) : default);
        string Of(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        Summary.Text = string.Join(" · ", new[]
        {
            Cloudflare.AccountName,
            workers is null ? "" : Of(workers.Count, "Worker", "Workers"),
            pages is null ? "" : Of(pages.Count, "Pages project", "Pages projects"),
            zones is null ? "" : Of(zones.Count, "domain", "domains"),
        }.Where(s => s.Length > 0));
    }

    void Kpis(Dictionary<string, Usage> use, string trafficError, AppRow? last)
    {
        var req = use.Values.Sum(u => u.Requests);
        var err = use.Values.Sum(u => u.Errors);
        var hourly = new float[24];
        foreach (var u in use.Values)
            for (var i = 0; i < 24; i++) hourly[i] += u.Hourly[i];
        var none = trafficError.Length > 0;
        ReqValue.Text = none ? "—" : Cloudflare.Compact(req);
        ReqSub.Text = none ? "Needs Account Analytics: Read" : $"{use.Count(u => u.Value.Requests > 0)} of {rows.Count(r => r.IsWorker)} Workers had traffic";
        ReqSub.ToolTip = none ? trafficError : null;
        ReqChart.Series = none ? null : hourly;
        ReqChart.Step = TimeSpan.FromHours(1);

        var rate = req > 0 ? err * 100.0 / req : 0;
        ErrValue.Text = none ? "—" : $"{rate:0.##}%";
        ErrValue.Foreground = Skin.Brush(rate >= 2 ? "Red" : rate >= 0.5 ? "Amber" : "Text");
        ErrSub.Text = none ? "" : err == 0 ? "No errors" : $"{err:N0} errors · most from {use.MaxBy(u => u.Value.Errors).Key}";

        var cpu = req > 0 ? use.Values.Sum(u => u.CpuMs * u.Requests) / req : 0;
        CpuValue.Text = none || req == 0 ? "—" : $"{cpu:0.#} ms";
        CpuSub.Text = none || req == 0 ? "" : $"busiest: {use.MaxBy(u => u.Value.CpuMs).Key}";

        LastCell.Tag = last;
        LastValue.Text = last?.Deploy.Ago ?? "—";
        LastSub.Text = last is null ? "" : $"{last.Deploy.Name} · {last.Deploy.StatusText}";
        LastSub.Foreground = last is { Deploy.Failed: true } ? Skin.Brush("Red") : Skin.Brush("Sub");
    }

    // ---- Projects list: kind chips, search, selection ----

    bool Matches(object o)
    {
        var r = (AppRow)o;
        if (kind == 1 && !r.IsWorker || kind == 2 && r.IsWorker) return false;
        var q = Search.Text.Trim();
        return q.Length == 0 || r.Deploy.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Deploy.Message.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Deploy.Branch.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void SearchChanged(object sender, TextChangedEventArgs e)
    {
        view?.Refresh();
        Count();
        Empty();
    }

    void Count()
    {
        var shown = view?.Cast<object>().Count() ?? 0;
        AppsCount.Text = !loaded && rows.Count == 0 ? "" : shown == rows.Count
            ? $"{rows.Count(r => r.IsWorker)} Workers · {rows.Count(r => !r.IsWorker)} Pages"
            : $"{shown} of {rows.Count} shown";
    }

    void Empty()
    {
        if (AppsNote.Text.Length > 0 && !AppsNote.Text.StartsWith("No") && !AppsNote.Text.StartsWith("Nothing")) return; // an error is showing
        AppsNote.Text = rows.Count == 0 ? "No Workers or Pages projects yet. Deploy one with wrangler or connect a repo in Pages."
            : Apps.Items.Count == 0 ? $"Nothing matches “{Search.Text.Trim()}”." : "";
    }

    async void AppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Apps.SelectedItem is not AppRow row)
        {
            Detail.Visibility = Visibility.Collapsed;
            Activity.Visibility = Visibility.Visible;
            return;
        }
        Apps.ScrollIntoView(row);
        var same = (Detail.DataContext as AppRow)?.Deploy.Key == row.Deploy.Key && Detail.Visibility == Visibility.Visible;
        Detail.DataContext = row;
        Activity.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;
        if (!same) { History.ItemsSource = null; HistoryNote.Text = "Loading…"; }

        var load = ++historyLoad;
        var (list, error) = await Try(Cloudflare.History(row.Deploy.Key));
        if (load != historyLoad) return; // another project was picked meanwhile
        HistoryNote.Text = list is null ? error : list.Count == 0 ? "None yet" : $"Last {list.Count}";
        HistoryNote.ToolTip = list is null ? error : null;
        if (list is not null) History.ItemsSource = list.Select((d, i) => new DeployView(d) { Last = i == list.Count - 1 }).ToList();
    }

    void CloseDetail(object sender, RoutedEventArgs e) => Apps.SelectedItem = null;

    void FeedClick(object sender, MouseButtonEventArgs e) => Pick((AppRow)((FrameworkElement)sender).DataContext);

    void OpenLast(object sender, MouseButtonEventArgs e)
    {
        if (LastCell.Tag is AppRow row) Pick(row);
    }

    /// Selects a project, clearing a filter that would hide it.
    void Pick(AppRow row)
    {
        if (view?.Contains(row) == false || !Matches(row))
        {
            Search.Text = "";
            ((RadioButton)KindChips.Children[0]).IsChecked = true;
        }
        Apps.SelectedItem = row;
    }

    // ---- Links and copying ----

    static AppRow RowOf(object sender) => (AppRow)((FrameworkElement)sender).DataContext;

    void OpenSite(object sender, RoutedEventArgs e) => Shell.OpenLink(RowOf(sender).Deploy.Url);

    void OpenInDash(object sender, RoutedEventArgs e) => Shell.OpenLink(RowOf(sender).Dash);

    void CopyUrl(object sender, RoutedEventArgs e) => Copy(RowOf(sender).Deploy.Url);

    void Navigate(object sender, RequestNavigateEventArgs e)
    {
        Shell.OpenLink(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    void HistoryClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeployView { HasUrl: true } d) Shell.OpenLink(d.Deploy.Url);
    }

    void OpenZone(object sender, MouseButtonEventArgs e) => Shell.OpenLink(Cloudflare.Dash(((ZoneView)((FrameworkElement)sender).DataContext).Zone.Name));

    void OpenStore(object sender, MouseButtonEventArgs e) => Shell.OpenLink(((StoreView)((FrameworkElement)sender).DataContext).Store.Dash);

    void OpenDashboard(object sender, RoutedEventArgs e) => Shell.OpenLink(Cloudflare.Dash());

    void MoreClick(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreButton.ContextMenu.IsOpen = true;
    }

    void CopyAccountId(object sender, RoutedEventArgs e) => Copy(Cloudflare.AccountId);

    void Copy(string text)
    {
        ToastText.Text = ClipboardHistory.Copy(text) ? "Copied" : "Clipboard busy, try again";
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    void Refresh(object sender, RoutedEventArgs e) => Load();

    // ---- Connecting ----

    void CreateToken(object sender, RoutedEventArgs e) => Shell.OpenLink(Cloudflare.NewTokenPage);

    void TokenKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ConnectClick(sender, e);
        e.Handled = true;
    }

    async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (TokenBox.Text.Trim().Length == 0) { Status("Paste the token first.", "Sub"); return; }
        ConnectButton.IsEnabled = false;
        Status("Checking the token…", "Sub");
        var (_, error) = await Try(Cloudflare.Connect(TokenBox.Text));
        ConnectButton.IsEnabled = true;
        Status(error, "Red");
        if (error.Length > 0) return;
        TokenBox.Text = "";
        loaded = false;
        Load();
    }

    void Status(string text, string color)
    {
        ConnectStatus.Text = text;
        ConnectStatus.Foreground = Skin.Brush(color);
    }

    void Reconnect(object sender, RoutedEventArgs e)
    {
        Cloudflare.Disconnect();
        Forget();
    }

    void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm("Cloudflare", "Disconnect Cloudflare?", "Glance forgets the token. Deploy alerts stop until you connect again.", "Disconnect")) return;
        Cloudflare.Disconnect();
        Forget();
    }

    void Forget()
    {
        rows = [];
        Apps.ItemsSource = Feed.ItemsSource = Zones.ItemsSource = Stores.ItemsSource = History.ItemsSource = null;
        loaded = false;
        Load();
    }
}
