using System.Diagnostics.Eventing.Reader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Glance.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Glance.Pages;

/// What has been heavy over a range, as a dashboard: a verdict, KPIs, CPU/memory load over time with Windows' events
/// on a lane underneath, the apps that used the most of each resource, and what Windows itself logged.
public partial class AnalysisPage : UserControl
{
    public record Consumer(int Rank, string Name, string Avg, string Peak, string Active, GridLength Fill, GridLength Rest, Brush Color);

    // Bucket sizes keep the chart at ~60–300 points, as on History.
    static readonly (string Label, TimeSpan Span, int Bucket)[] Ranges =
    [
        ("1h", TimeSpan.FromHours(1), 1), ("6h", TimeSpan.FromHours(6), 2), ("24h", TimeSpan.FromDays(1), 5),
        ("7d", TimeSpan.FromDays(7), 30), ("30d", TimeSpan.FromDays(30), 120),
    ];
    // Same colors as the History charts.
    static readonly (string Key, string Title, int Rgb)[] Kinds = [("cpu", "CPU", 0xE6A65D), ("gpu", "GPU", 0x4CC37A), ("ram", "Memory", 0xB07CFF), ("disk", "Disk", 0x3DD6C6)];

    // A strained minute: the CPU near flat out, or memory nearly full (Windows starts paging).
    const double CpuLimit = 85, RamLimit = 0.9;
    static readonly double RamTotalGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;

    int range = 2, kind;
    int loadId;
    DateTime loadedAt;
    List<(string Name, string Res, double Sum, double Peak, int Active)> sums = [];
    int minutes, strained;
    string? culprit;
    List<Report>? reports;
    Dictionary<(int, string), string> points = []; // chart hover: the apps behind each point

    public AnalysisPage()
    {
        InitializeComponent();
        for (var i = 0; i < Ranges.Length; i++)
        {
            var index = i;
            var chip = new RadioButton { Content = Ranges[i].Label, Style = Skin.Style("Chip"), GroupName = "analysisRange", IsChecked = i == range };
            chip.Checked += (_, _) => { range = index; Load(); };
            RangeChips.Children.Add(chip);
        }
        for (var i = 0; i < Kinds.Length; i++)
        {
            var index = i;
            var chip = new RadioButton { Content = Kinds[i].Title, Style = Skin.Style("Chip"), GroupName = "analysisKind", IsChecked = i == kind };
            chip.Checked += (_, _) => { kind = index; ShowConsumers(); };
            KindChips.Children.Add(chip);
        }
        CpuChart.Detail = i => points.GetValueOrDefault((i, "cpu"));
        GpuChart.Detail = i => points.GetValueOrDefault((i, "gpu"));
        MemChart.Detail = i => points.GetValueOrDefault((i, "ram"));
        Events.SizeChanged += (_, _) => PlaceEvents();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || ReportDetail.Visibility != Visibility.Visible) return;
            CloseReport(this, e);
            e.Handled = true;
        };
        // Samples arrive once a minute, so refetching more often than that is pointless.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > TimeSpan.FromMinutes(1)) Load(); };
    }

    void Load()
    {
        loadedAt = DateTime.Now;
        var id = ++loadId; // a later pick wins over a slower earlier load
        LoadHistory(id);
        LoadReports(id);
    }

    async void LoadHistory(int id)
    {
        var (label, span, bucket) = Ranges[range];
        var from = DateTime.UtcNow - span;
        BsonDocument? doc;
        List<BsonDocument> history;
        try
        {
            var analysis = Db.Analysis(span, CpuLimit, RamTotalGb * RamLimit);
            history = await Db.History(span, bucket);
            doc = await analysis;
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            if (id == loadId) (Status.Text, loadedAt) = ("MongoDB not reachable", default);
            return;
        }
        if (id != loadId) return;

        static BsonArray Facet(BsonDocument? d, string name) => d?[name].AsBsonArray ?? [];
        static int Count(BsonDocument? d, string name) => Facet(d, name).FirstOrDefault() is BsonDocument c ? c["n"].ToInt32() : 0;
        (minutes, strained) = (Count(doc, "minutes"), Count(doc, "strained"));
        Status.Text = minutes == 0 ? "" : $"Based on {Duration(minutes)} of samples";
        sums = Facet(doc, "apps").Select(a => (a["_id"]["n"].AsString, a["_id"]["r"].AsString, a["v"].ToDouble(), a["p"].ToDouble(), a["m"].ToInt32())).ToList();
        var culprits = Facet(doc, "culprits").Select(c => $"{c["_id"]["n"].AsString} ({(c["_id"]["r"] == "ram" ? "memory" : "CPU")})  {Duration(c["m"].ToInt32())}").ToList();
        culprit = Facet(doc, "culprits").FirstOrDefault()?["_id"]["n"].AsString;

        // Chart: CPU, GPU and memory on one 0–100% scale.
        var cpu = HistoryPage.Grid(history, "cpu", span, bucket, from);
        var gpu = HistoryPage.Grid(history, "gpu", span, bucket, from);
        var ramGb = HistoryPage.Grid(history, "ramGb", span, bucket, from);
        CpuChart.Step = GpuChart.Step = MemChart.Step = TimeSpan.FromMinutes(bucket);
        CpuChart.Series = cpu;
        GpuChart.Series = gpu;
        MemChart.Series = ramGb.Select(v => (float)(v / RamTotalGb * 100)).ToArray();
        ChartFrom.Text = "−" + label;
        ChartNone.Visibility = minutes == 0 ? Visibility.Visible : Visibility.Collapsed;

        (CpuAvg.Text, CpuSub.Text) = HistoryPage.Summary(cpu) is { } c ? ($"{c.Avg:0}%", $"peak {c.Peak:0}%") : ("—", "");
        (GpuAvg.Text, GpuSub.Text) = HistoryPage.Summary(gpu) is { } gs ? ($"{gs.Avg:0}%", $"peak {gs.Peak:0}%") : ("—", "");
        (MemAvg.Text, MemSub.Text) = HistoryPage.Summary(ramGb) is { } m ? ($"{m.Avg:0.0} GB", $"peak {m.Peak:0.0} of {RamTotalGb:0} GB") : ("—", "");
        StrainValue.Text = minutes == 0 ? "—" : Duration(strained);
        StrainSub.Text = minutes == 0 ? "" : strained == 0 ? "never near the limit" : $"{strained * 100.0 / minutes:0.#}% of the time · mostly {culprit}";
        StrainCell.ToolTip = $"Minutes with the CPU at {CpuLimit}%+ or memory {RamLimit:0%} full."
            + (culprits.Count > 0 ? "\nOn top then:\n" + string.Join("\n", culprits) : "");

        ShowConsumers();
        ShowVerdict();

        points = [];
        try { var p = await HistoryPage.AppsByPoint(span, bucket, from); if (id == loadId) points = p; }
        catch (Exception e) when (e is MongoException or TimeoutException) { } // the chart still works without it
    }

    void ShowConsumers()
    {
        var (key, _, rgb) = Kinds[kind];
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        var rows = sums.Where(s => s.Res == key).OrderByDescending(s => s.Sum).Take(5).ToList();
        var max = rows.Count > 0 ? rows[0].Sum : 1;
        // An app missing from a minute's top 5 used ~0 then, so averaging over every minute is fair.
        Consumers.ItemsSource = rows.Select((s, i) => new Consumer(i + 1, s.Name,
            HistoryPage.Amount(key, s.Sum / minutes), HistoryPage.Amount(key, s.Peak), Duration(s.Active),
            new GridLength(s.Sum / max, GridUnitType.Star), new GridLength(1 - s.Sum / max, GridUnitType.Star), brush)).ToArray();
        ConsumersNone.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void LoadReports(int id)
    {
        List<Report> list;
        string? error = null;
        try { list = await Task.Run(() => WinReports.Read(Ranges[range].Span)); }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            (list, error) = ([], "Couldn't read the Windows event logs: " + e.Message);
        }
        if (id != loadId) return;
        reports = list;
        Reports.ItemsSource = list;
        ReportsNone.Text = error ?? "Nothing reported: no crashes, freezes, or disk or driver trouble.";
        ReportsNone.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The KPI counts what matters; harmless noise stays in the list only.
        var real = list.Where(r => r.Tier != Tier.Minor).ToList();
        var serious = real.Count(r => r.Serious);
        IssuesValue.Text = real.Sum(r => r.Count).ToString();
        IssuesSub.Text = real.Count == 0 ? "nothing that matters" : $"{serious} serious · last {real.MaxBy(r => r.Last)!.LastText}";
        PlaceEvents();
        ShowVerdict();
        // A report left open follows the new range, or closes if it isn't in it.
        if (ReportDetail.DataContext is Report open)
        {
            if (list.Find(r => r.Title == open.Title) is { } again) ShowReport(again);
            else CloseReport(this, new RoutedEventArgs());
        }
    }

    void OpenReport(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowReport((Report)((FrameworkElement)sender).DataContext);

    void ShowReport(Report r)
    {
        ReportDetail.DataContext = r;
        var color = r.Tier switch { Tier.Serious => "Red", Tier.Problem => "Amber", _ => "Sub" };
        var c = Skin.BrushColor(color);
        TierPill.Background = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        TierLabel.Foreground = Skin.Brush(color);
        ReportList.Visibility = Visibility.Collapsed;
        ReportDetail.Visibility = Visibility.Visible;
    }

    void CloseReport(object sender, RoutedEventArgs e)
    {
        ReportDetail.DataContext = null;
        ReportDetail.Visibility = Visibility.Collapsed;
        ReportList.Visibility = Visibility.Visible;
    }

    Report? Shown => ReportDetail.DataContext as Report;

    void SearchReport(object sender, RoutedEventArgs e) => Shell.OpenLink(Shown?.SearchUrl);

    void OpenEventViewer(object sender, RoutedEventArgs e)
    {
        try { Shell.Open("eventvwr.msc"); }
        catch (System.ComponentModel.Win32Exception) { } // cancelled
    }

    void CopyReport(object sender, RoutedEventArgs e)
    {
        if (Shown is { } r) ClipboardHistory.Copy(r.ToString());
    }

    // One headline for the range: serious Windows reports beat strain, which beats app crashes.
    void ShowVerdict()
    {
        if (reports is null) return; // still loading; the header says "Analyzing…"
        var serious = reports.Where(r => r.Serious).ToList();
        var pct = minutes > 0 ? strained * 100.0 / minutes : 0;
        var (color, glyph, title, sub) =
            serious.Count > 0 ? ("Red", "", "Needs attention", Names(serious))
            : pct >= 5 ? ("Amber", "", "Running hot", $"Under strain {pct:0}% of the time, mostly {culprit}")
            : reports.Where(r => r.Tier == Tier.Problem).ToList() is { Count: > 0 } problems ? ("Amber", "", "Mostly fine", Names(problems))
            : ("Green", "", "Looking good", "No strain, and nothing reported by Windows");
        var c = Skin.BrushColor(color);
        VerdictIcon.Text = glyph;
        VerdictIcon.Foreground = Skin.Brush(color);
        VerdictBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        VerdictTitle.Text = title;
        VerdictSub.Text = sub;
    }

    static string Names(List<Report> rs) => string.Join(" · ", rs.Take(2).Select(r => r.Title)) + (rs.Count > 2 ? $" · and {rs.Count - 2} more" : "");

    // Windows' events as dots on the lane under the chart; ones within a few pixels share a dot (red if any is serious).
    void PlaceEvents()
    {
        Events.Children.Clear();
        var w = Events.ActualWidth;
        if (reports is null || w <= 0) return;
        const double Cell = 10;
        var span = Ranges[range].Span;
        var from = DateTime.Now - span;
        foreach (var dot in reports.Where(r => r.Tier != Tier.Minor).SelectMany(r => r.Times.Select(t => (r, t))).Where(e => e.t >= from)
                     .GroupBy(e => (int)((e.t - from) / span * w / Cell)))
        {
            var lines = dot.GroupBy(e => e.r).Select(g => g.Key.Title + (g.Count() > 1 ? $"  ×{g.Count()}" : ""));
            var top = dot.OrderByDescending(e => e.r.Tier).ThenByDescending(e => e.t).First().r; // what a click opens
            var mark = new Ellipse { Width = 9, Height = 9, Fill = Skin.Brush(top.Serious ? "Red" : "Amber"), IsHitTestVisible = false };
            // A bigger, invisible target than the dot itself, so it's easy to hover and click.
            var hit = new Border
            {
                Width = 16, Height = 16, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = mark,
                ToolTip = Report.When(dot.Min(e => e.t)) + "\n" + string.Join("\n", lines) + "\nClick for details",
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
            };
            hit.MouseEnter += (_, _) => ((ScaleTransform)hit.RenderTransform).ScaleX = ((ScaleTransform)hit.RenderTransform).ScaleY = 1.35;
            hit.MouseLeave += (_, _) => ((ScaleTransform)hit.RenderTransform).ScaleX = ((ScaleTransform)hit.RenderTransform).ScaleY = 1;
            hit.MouseLeftButtonUp += (_, _) => ShowReport(top);
            Canvas.SetLeft(hit, Math.Min(dot.Key * Cell, w - 16));
            Canvas.SetTop(hit, 1);
            Events.Children.Add(hit);
        }
    }

    static string Duration(int minutes) => minutes switch
    {
        < 120 => $"{minutes} min",
        < 2880 => $"{minutes / 60.0:0.#} h",
        _ => $"{minutes / 1440.0:0.#} days",
    };
}
