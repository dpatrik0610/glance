using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Glance.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Glance.Pages;

public partial class HistoryPage : UserControl
{
    public record Chart(string Title, string Stats, string From, float[] Series, Color Color, float Max, string Format, TimeSpan Step,
        Func<int, string?>? Detail);

    // ~60–360 points per chart regardless of range; Mongo does the averaging.
    static readonly (string Label, TimeSpan Span, int Bucket)[] Ranges =
    [
        ("1h", TimeSpan.FromHours(1), 1), ("24h", TimeSpan.FromDays(1), 5), ("7d", TimeSpan.FromDays(7), 30), ("30d", TimeSpan.FromDays(30), 120),
    ];

    int range = 1;
    DateTime loadedAt;

    public HistoryPage()
    {
        InitializeComponent();
        for (var i = 0; i < Ranges.Length; i++)
        {
            var index = i;
            var chip = new RadioButton { Content = Ranges[i].Label, Style = (Style)FindResource("Chip"), GroupName = "range", IsChecked = i == range };
            chip.Checked += (_, _) => { range = index; Load(); };
            RangeChips.Children.Add(chip);
        }
        // New samples arrive once a minute, so refetching more often than that is pointless.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > TimeSpan.FromMinutes(1)) Load(); };
    }

    async void Load()
    {
        var (label, span, bucket) = Ranges[range];
        loadedAt = DateTime.Now;
        loading = true;
        List<BsonDocument> docs;
        try { docs = await Db.History(span, bucket); }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            Status.Text = "MongoDB not reachable.";
            loadedAt = default;
            return;
        }
        finally { loading = false; }
        Status.Text = docs.Count == 0 ? "No data yet: one sample is logged per minute." : "";

        // Fixed time grid; buckets with no data stay NaN and draw as gaps (app wasn't running).
        var n = (int)(span.TotalMinutes / bucket);
        var from = DateTime.UtcNow - span;
        float[] Col(string field)
        {
            var a = new float[n];
            Array.Fill(a, float.NaN);
            foreach (var d in docs)
            {
                var i = (int)((d["_id"].ToUniversalTime() - from).TotalMinutes / bucket);
                if (i >= 0 && i < n && d.TryGetValue(field, out var v) && v.IsNumeric) a[i] = (float)v.ToDouble();
            }
            return a;
        }

        // resource: which apps' use explains this chart, shown on hover (null: Windows doesn't count it per app).
        Chart Make(string title, string field, string format, int rgb, float max, string? resource = null)
        {
            var a = Col(field);
            var stats = Summary(a) is { } s ? $"avg {Spark.Text(format, s.Avg)} · peak {Spark.Text(format, s.Peak)}" : "no data";
            return new(title, stats, "−" + label, a, Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), max,
                format, TimeSpan.FromMinutes(bucket), resource is null ? null : i => apps.GetValueOrDefault((i, resource)));
        }

        Charts.ItemsSource = charts = new[]
        {
            Make("CPU load", "cpu", "{0:0}%", 0xFF6A3D, 100, "cpu"),
            Make("GPU load", "gpu", "{0:0}%", 0x4CC37A, 100, "gpu"),
            Make("CPU temperature", "cpuTemp", "{0:0}°C", 0xFF4D5E, 100, "cpu"),
            Make("GPU temperature", "gpuTemp", "{0:0}°C", 0xF5B83D, 100, "gpu"),
            Make("Memory", "ramGb", "{0:0.0} GB", 0xB07CFF, 0, "ram"),
            Make("Disk I/O", "disk", "rate", 0x3DD6C6, 0, "disk"),
            Make("Download", "netDown", "rate", 0x5AA9FF, 0),
            Make("Upload", "netUp", "rate", 0xE879C6, 0),
        };
        ShowSpotlight();
        LoadApps(span, bucket, from);
    }

    // (chart point, resource) → the apps behind it, one per line. Filled after the charts so they never wait on it.
    Dictionary<(int, string), string> apps = [];

    async void LoadApps(TimeSpan span, int bucket, DateTime from)
    {
        apps = [];
        List<BsonDocument> rows;
        try { rows = await Db.TopApps(span, bucket); }
        catch (Exception e) when (e is MongoException or TimeoutException) { return; } // the charts still work without it
        if (Ranges[range].Span != span) return; // another range was picked meanwhile
        var map = new Dictionary<(int, string), string>();
        foreach (var row in rows)
        {
            var (b, r) = (row["_id"]["b"].ToUniversalTime(), row["_id"]["r"].AsString);
            map[((int)((b - from).TotalMinutes / bucket), r)] = string.Join("\n", row["apps"].AsBsonArray.Select(a =>
                a["n"].AsString + "  " + Amount(r, a["v"].ToDouble())));
        }
        apps = map;
    }

    static string Amount(string resource, double v) => resource switch
    {
        "ram" => v >= 1024 ? $"{v / 1024:0.0} GB" : $"{v:0} MB",
        "disk" => SystemPage.Rate(v),
        _ => $"{v:0.#}%",
    };

    Chart[] charts = [];
    string? spotlight;
    bool loading;

    /// Rings one chart briefly so the eye lands on it (when a System tile was clicked). Waits for the data if it's still loading.
    public void Spotlight(string title)
    {
        spotlight = title;
        if (!loading && charts.Length > 0) ShowSpotlight(); // else Load does it once the fresh charts are in
    }

    void ShowSpotlight() => Dispatcher.BeginInvoke(() =>
    {
        if (spotlight is null || charts.FirstOrDefault(c => c.Title == spotlight) is not { } chart) return;
        spotlight = null;
        if (Charts.ItemContainerGenerator.ContainerFromItem(chart) is not ContentPresenter cp
            || VisualTreeHelper.GetChildrenCount(cp) == 0 || VisualTreeHelper.GetChild(cp, 0) is not Border card) return;
        var ring = new SolidColorBrush(((SolidColorBrush)FindResource("Accent")).Color) { Opacity = 0 };
        card.BorderBrush = ring;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.9) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25))));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.1))));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.9)), new CubicEase()));
        pulse.Completed += (_, _) => card.ClearValue(Border.BorderBrushProperty); // back to the style's (and its hover)
        ring.BeginAnimation(Brush.OpacityProperty, pulse);
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    static (float Avg, float Peak)? Summary(float[] a)
    {
        double sum = 0;
        int count = 0;
        var peak = float.MinValue;
        foreach (var v in a)
            if (!float.IsNaN(v)) (sum, count, peak) = (sum + v, count + 1, Math.Max(peak, v));
        return count == 0 ? null : ((float)(sum / count), peak);
    }
}
