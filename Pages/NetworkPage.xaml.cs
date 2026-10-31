using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

public partial class NetworkPage : UserControl
{
    static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    readonly Queue<long?> pings = new(); // last minute of internet pings, null = lost
    NetworkInterface[] online = [];
    IPAddress? gateway;
    long lastRx, lastTx, lastAt;
    List<ConnInfo> conns = [];
    DateTime ipAt;
    volatile bool stale = true; // adapters/addresses changed since the last look
    bool busy;
    int ticks;

    public NetworkPage()
    {
        InitializeComponent();
        timer.Tick += Tick;
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        NetworkChange.NetworkAddressChanged += (_, _) => stale = true;
        // Everything here only runs while the page is on screen.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) { timer.Stop(); return; }
            timer.Start();
            Tick(null, EventArgs.Empty);
        };
    }

    async void Tick(object? sender, EventArgs e)
    {
        if (busy) return; // a ping can take up to its 1 s timeout
        busy = true;
        try
        {
            if (stale || DateTime.Now - ipAt > TimeSpan.FromMinutes(10)) LoadIp();
            if (stale)
            {
                stale = false;
                online = Net.Online();
                gateway = Net.Gateway(online);
                Adapters.ItemsSource = Net.Adapters();
                lastAt = 0; // different adapters: byte totals aren't comparable
            }
            Throughput();
            if (ticks++ % 3 == 0) LoadConnections();
            var (net, gw) = (Net.Ping(Internet), Net.Ping(gateway));
            Latency(await net, await gw);
        }
        finally { busy = false; }
    }

    void Throughput()
    {
        long rx = 0, tx = 0;
        foreach (var n in online)
        {
            try { var s = n.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; }
            catch (NetworkInformationException) { } // adapter vanished; stale will be set
        }
        var now = Stopwatch.GetTimestamp();
        if (lastAt != 0)
        {
            var dt = Stopwatch.GetElapsedTime(lastAt, now).TotalSeconds;
            var (down, up) = (Math.Max(0, (rx - lastRx) / dt), Math.Max(0, (tx - lastTx) / dt));
            DownRate.Text = SystemPage.Rate(down);
            UpRate.Text = SystemPage.Rate(up);
            DownSpark.Push((float)down);
            UpSpark.Push((float)up);
        }
        DownTotal.Text = Size(rx) + " since link up";
        UpTotal.Text = Size(tx) + " since link up";
        (lastRx, lastTx, lastAt) = (rx, tx, now);
    }

    static string Size(long bytes) => bytes >= 1e9 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";

    void Latency(long? net, long? gw)
    {
        pings.Enqueue(net);
        if (pings.Count > 60) pings.Dequeue();
        var ok = pings.OfType<long>().ToArray();
        var jitter = ok.Length > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        PingText.Text = net is { } ms ? $"{ms} ms" : "timeout";
        PingSub.Text = $"router {(gw is { } g ? $"{g} ms" : "—")} · ±{jitter:0} ms · {100.0 * (pings.Count - ok.Length) / pings.Count:0}% loss";
        PingSpark.Push(net);
    }

    async void LoadIp()
    {
        ipAt = DateTime.Now;
        try
        {
            var ip = await Net.PublicIp();
            (IpText.Text, IpWhere.Text) = (ip.Ip, ip.Where);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ipAt = DateTime.Now - TimeSpan.FromMinutes(9); // try again in a minute
            (IpText.Text, IpWhere.Text) = ("offline", "Couldn't reach ipinfo.io");
            Db.Error("public ip", e);
        }
    }

    async void LoadConnections()
    {
        conns = await Task.Run(Native.Connections);
        ConnCount.Text = $"{conns.Count} open";
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var f = Filter.Text.Trim();
        Conns.ItemsSource = f.Length == 0 ? conns
            : conns.Where(c => c.Process.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Remote.Contains(f) || c.Port.ToString() == f).ToList();
    }

    void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void RefreshIp(object sender, RoutedEventArgs e) => LoadIp();

    void CopyIp(object sender, RoutedEventArgs e)
    {
        if (IPAddress.TryParse(IpText.Text, out _)) Copy(IpText.Text);
    }

    void CopyTag(object sender, RoutedEventArgs e) => Copy((string)((FrameworkElement)sender).Tag);

    void Copy(string text)
    {
        ToastText.Text = ClipboardHistory.Copy(text) ? $"Copied {text}" : "Clipboard busy, try again";
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    async void RunSpeed(object sender, RoutedEventArgs e)
    {
        (SpeedButton.IsEnabled, SpeedButton.Content) = (false, "Testing…");
        SpeedDown.Text = SpeedUp.Text = SpeedPing.Text = "—";
        SpeedBar.Visibility = Visibility.Visible;
        try
        {
            // Idle ping, before the line is loaded: middle of 5 so one slow reply doesn't skew it.
            var rtts = new List<long>();
            for (var i = 0; i < 5; i++) if (await Net.Ping(Internet) is { } ms) rtts.Add(ms);
            SpeedPing.Text = rtts.Count > 0 ? rtts.Order().ElementAt(rtts.Count / 2).ToString() : "×";
            Phase(DownArrow, "Testing download…");
            SpeedDown.Text = Mbps(await Net.SpeedTest(false, v => SpeedDown.Text = Mbps(v)));
            Phase(UpArrow, "Testing upload…");
            SpeedUp.Text = Mbps(await Net.SpeedTest(true, v => SpeedUp.Text = Mbps(v)));
            SpeedNote.Text = $"Cloudflare · tested {DateTime.Now:HH:mm}";
            Db.Info($"speedtest down={SpeedDown.Text} up={SpeedUp.Text} Mbps ping={SpeedPing.Text} ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            SpeedNote.Text = "Test failed: " + ex.Message;
            Db.Error("speedtest", ex);
        }
        finally
        {
            Phase(null, null);
            (SpeedButton.IsEnabled, SpeedButton.Content) = (true, "Run test");
            SpeedBar.Visibility = Visibility.Collapsed;
        }
    }

    /// The measured direction's arrow pulses and the bar fills in its color; null = test over.
    void Phase(TextBlock? arrow, string? note)
    {
        DownArrow.BeginAnimation(OpacityProperty, null);
        UpArrow.BeginAnimation(OpacityProperty, null);
        if (arrow is null) return;
        SpeedNote.Text = note;
        arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(550))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        SpeedBar.Foreground = arrow.Foreground;
        SpeedBar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(7))); // SpeedTest's run time
    }

    static string Mbps(double v) => v >= 100 ? $"{v:0}" : $"{v:0.0}";
}
