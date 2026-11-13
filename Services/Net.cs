using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Glance.Services;

public record Field(string Label, string[] Values);
public record Adapter(string Name, string Detail, Field[] Fields);
public record PublicIp(string Ip, string Where);

public static partial class Net
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly HttpClient bulk = new() { Timeout = TimeSpan.FromSeconds(30) };

    record IpInfo(string Ip, string? City, string? Country, string? Org);

    /// ipinfo.io: free, no key. Org comes as "AS35311 PR-TELECOM Zrt."; the AS number is noise here.
    public static async Task<PublicIp> PublicIp()
    {
        var i = await http.GetFromJsonAsync<IpInfo>("https://ipinfo.io/json") ?? throw new HttpRequestException("empty response");
        string[] parts = [AsNumber().Replace(i.Org ?? "", ""), string.Join(", ", new[] { i.City, i.Country }.Where(s => !string.IsNullOrEmpty(s)))];
        return new(i.Ip, string.Join(" · ", parts.Where(p => p.Length > 0)));
    }

    [GeneratedRegex(@"^AS\d+\s+")] private static partial Regex AsNumber();

    /// Adapters that are up and have an address (skips the filter drivers Windows lists per NIC); the one with a gateway first.
    public static Adapter[] Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => (n, ip: n.GetIPProperties()))
        .Where(x => x.ip.UnicastAddresses.Count > 0)
        .OrderByDescending(x => x.ip.GatewayAddresses.Count > 0)
        .Select(x =>
        {
            var (n, ip) = x;
            var addrs = ip.UnicastAddresses.Select(a => a.Address).ToArray();
            var mac = n.GetPhysicalAddress().GetAddressBytes();
            Field?[] fields =
            [
                Ips("IPv4", addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork)),
                Ips("IPv6", addrs.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal)),
                Ips("Gateway", ip.GatewayAddresses.Select(g => g.Address)),
                Ips("DNS", ip.DnsAddresses.Where(a => !a.IsIPv6SiteLocal)), // fec0:: entries are Windows' unused defaults
                mac.Length == 6 ? new("MAC", [string.Join(":", mac.Select(b => b.ToString("X2")))]) : null,
            ];
            var speed = n.Speed >= 1e9 ? $"{n.Speed / 1e9:0.#} Gbps" : $"{n.Speed / 1e6:0} Mbps";
            return new Adapter(n.Name, $"{n.Description} · {speed}", fields.OfType<Field>().ToArray());
        }).ToArray();

    static Field? Ips(string label, IEnumerable<IPAddress> ips)
    {
        var values = ips.Select(i => i.ToString()).Distinct().ToArray();
        return values.Length > 0 ? new(label, values) : null;
    }

    /// Adapters with a way out: their byte counters are this PC's internet traffic.
    public static NetworkInterface[] Online() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0).ToArray();

    public static IPAddress? Gateway(NetworkInterface[] online) => online
        .SelectMany(n => n.GetIPProperties().GatewayAddresses)
        .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// Round trip in ms, null = lost.
    public static async Task<long?> Ping(IPAddress? host)
    {
        if (host is null) return null;
        using var p = new Ping();
        try
        {
            var r = await p.SendPingAsync(host, 1000);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }

    // ---- Speed test: Cloudflare (what speed.cloudflare.com itself uses), falling back to a public LibreSpeed server ----

    public record SpeedServer(string Name, string Down, string Up);

    public static readonly SpeedServer[] SpeedServers =
    [
        // 50 MB a request (100 MB is refused): a fast line goes through many, and too many in a few minutes get a 429
        new("Cloudflare", "https://speed.cloudflare.com/__down?bytes=50000000", "https://speed.cloudflare.com/__up"),
        // From LibreSpeed's public list (librespeed.org/backend-servers/servers.php); kept up with a 600 Mbps line in Hungary
        new("CESNET Prague", "https://speedtest.cesnet.cz/backend/garbage.php?ckSize=100", "https://speedtest.cesnet.cz/backend/empty.php"),
    ];

    /// Mbps over ~6 s with 4 parallel streams (one TCP stream rarely fills a fast line).
    /// The first second is TCP ramp-up and isn't counted. Reports the running figure as it goes.
    public static async Task<double> SpeedTest(SpeedServer server, bool upload, Action<double> progress)
    {
        long bytes = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Transfer(server, upload, n => Interlocked.Add(ref bytes, n), stop.Token))).ToArray();
        await Task.Delay(1000);
        var (from, at) = (Interlocked.Read(ref bytes), Stopwatch.GetTimestamp());
        double mbps = 0;
        while (!stop.IsCancellationRequested && workers.Any(w => !w.IsCompleted))
        {
            await Task.Delay(250);
            mbps = (Interlocked.Read(ref bytes) - from) * 8 / Stopwatch.GetElapsedTime(at).TotalSeconds / 1e6;
            progress(mbps);
        }
        await Task.WhenAll(workers); // a real failure (offline, blocked) surfaces here
        return mbps;
    }

    static async Task Transfer(SpeedServer server, bool upload, Action<int> add, CancellationToken stop)
    {
        var buf = new byte[64 << 10];
        try
        {
            while (true)
            {
                if (upload)
                {
                    var body = new Payload(buf, add, stop);
                    body.Headers.ContentType = new("application/octet-stream"); // so a PHP backend doesn't try to parse 8 MB as a form
                    using var r = await bulk.PostAsync(server.Up, body, stop);
                    Ok(r);
                }
                else
                {
                    using var r = await bulk.GetAsync(server.Down, HttpCompletionOption.ResponseHeadersRead, stop);
                    Ok(r);
                    await using var s = await r.Content.ReadAsStreamAsync(stop);
                    for (int n; (n = await s.ReadAsync(buf, stop)) > 0;) add(n);
                }
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { } // time's up mid-transfer
    }

    static void Ok(HttpResponseMessage r)
    {
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"{r.RequestMessage?.RequestUri?.Host} limits how often one connection can test. Try again in a few minutes.",
                null, r.StatusCode);
        r.EnsureSuccessStatusCode();
    }

    /// 8 MB of zeros, counted as it's written so a cut-off upload still counts what went out.
    sealed class Payload(byte[] chunk, Action<int> sent, CancellationToken stop) : HttpContent
    {
        const int Chunks = 128;

        protected override async Task SerializeToStreamAsync(Stream s, TransportContext? context)
        {
            for (var i = 0; i < Chunks; i++)
            {
                await s.WriteAsync(chunk, stop);
                sent(chunk.Length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)Chunks * chunk.Length;
            return true;
        }
    }
}

/// The one speed test, shared by the Network page and the bar's panel: either can start it, both show it as it runs.
/// Runs on the UI thread (started from a click), so Changed fires there too.
public static class SpeedRun
{
    public enum Stage { Idle, Ping, Down, Up, Done, Failed }

    static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");
    public const double PhaseSeconds = 7; // Net.SpeedTest's run time

    public static Stage Now { get; private set; }
    public static DateTime Since { get; private set; } // when Now began
    public static double? Ping, Jitter, Down, Up; // results so far (ms, ms, Mbps, Mbps)
    public static double Live; // what's being measured right now: ms while pinging, else Mbps
    public static List<float> Downs = [], Ups = []; // the running figure, 4 a second, per direction
    public static string Note { get; private set; } = "Cloudflare · about 15 s";
    public static bool Running => Now is Stage.Ping or Stage.Down or Stage.Up;
    public static event Action? Changed;

    public static async void Start()
    {
        if (Running) return;
        (Ping, Jitter, Down, Up, Downs, Ups) = (null, null, null, null, [], []);
        try
        {
            // Idle ping, before the line is loaded: the middle of 8 so one slow reply doesn't skew it. The gaps also give the
            // dial time for its warm-up sweep.
            Go(Stage.Ping, "Measuring latency…");
            var rtts = new List<long>();
            for (var i = 0; i < 8; i++)
            {
                if (await Net.Ping(Internet) is { } ms) { rtts.Add(ms); Live = ms; Changed?.Invoke(); }
                await Task.Delay(150);
            }
            if (rtts.Count > 0)
            {
                Ping = rtts.Order().ElementAt(rtts.Count / 2);
                Jitter = rtts.Count > 1 ? rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
            }
            var server = 0;
            Down = await Measure(false, Downs);
            Up = await Measure(true, Ups);
            Go(Stage.Done, $"{Net.SpeedServers[server].Name} · tested {DateTime.Now:HH:mm}");

            // A server that refuses (rate limit, down, blocked) hands this direction and the rest of the run to the next one.
            async Task<double> Measure(bool upload, List<float> samples)
            {
                while (true)
                {
                    var s = Net.SpeedServers[server];
                    Go(upload ? Stage.Up : Stage.Down, $"Testing {(upload ? "upload" : "download")} · {s.Name}…");
                    samples.Clear();
                    try { return await Net.SpeedTest(s, upload, v => { Live = v; samples.Add((float)v); Changed?.Invoke(); }); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && server + 1 < Net.SpeedServers.Length)
                    {
                        Db.Error($"speedtest: {s.Name} failed, trying {Net.SpeedServers[++server].Name}", ex);
                    }
                }
            }
            Db.Info($"speedtest down={Mbps(Down.Value)} up={Mbps(Up.Value)} Mbps ping={Ping?.ToString("0") ?? "×"} ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Go(Stage.Failed, "Test failed: " + ex.Message);
            Db.Error("speedtest", ex);
        }
    }

    static void Go(Stage s, string note)
    {
        (Now, Since, Note, Live) = (s, DateTime.Now, note, 0);
        Changed?.Invoke();
    }

    public static string Mbps(double v) => v >= 100 ? $"{v:0}" : $"{v:0.0}";
}
