using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Glance.Pages;

public partial class HistoryPage : UserControl
{
    public record Chart(string Title, string Stats, string From, float[] Series, Color Color, float Max);

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
        List<BsonDocument> docs;
        try { docs = await Db.History(span, bucket); }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            Status.Text = "MongoDB not reachable.";
            loadedAt = default;
            return;
        }
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

        Chart Make(string title, string field, Func<float, string> fmt, int rgb, float max)
        {
            var a = Col(field);
            var stats = Summary(a) is { } s ? $"avg {fmt(s.Avg)} · peak {fmt(s.Peak)}" : "no data";
            return new(title, stats, "−" + label, a, Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), max);
        }

        Charts.ItemsSource = new[]
        {
            Make("CPU load", "cpu", v => $"{v:0}%", 0xFF6A3D, 100),
            Make("GPU load", "gpu", v => $"{v:0}%", 0x4CC37A, 100),
            Make("CPU temperature", "cpuTemp", v => $"{v:0}°C", 0xFF4D5E, 100),
            Make("GPU temperature", "gpuTemp", v => $"{v:0}°C", 0xF5B83D, 100),
            Make("Memory", "ramGb", v => $"{v:0.0} GB", 0xB07CFF, 0),
            Make("Download", "netDown", v => SystemPage.Rate(v), 0x5AA9FF, 0),
        };
    }

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
