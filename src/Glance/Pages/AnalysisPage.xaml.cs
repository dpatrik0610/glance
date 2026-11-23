using System.Diagnostics.Eventing.Reader;
using System.Windows;
using System.Windows.Controls;
using Glance.Services;
using MongoDB.Bson;
using MongoDB.Driver;
using Busy = Glance.Pages.SystemPage.Busy;

namespace Glance.Pages;

/// What has been heavy over a range: the apps that used the most of each resource, the minutes the PC was strained
/// and who was on top then (from the per-minute history), and what Windows itself logged (crashes, disk trouble…).
public partial class AnalysisPage : UserControl
{
    public record Heavy(string Title, Busy[] Apps);

    static readonly (string Label, TimeSpan Span)[] Ranges =
    [
        ("1h", TimeSpan.FromHours(1)), ("6h", TimeSpan.FromHours(6)), ("24h", TimeSpan.FromDays(1)), ("7d", TimeSpan.FromDays(7)), ("30d", TimeSpan.FromDays(30)),
    ];
    static readonly (string Key, string Title)[] Kinds = [("cpu", "CPU"), ("gpu", "GPU"), ("ram", "Memory"), ("disk", "Disk")];

    // A strained minute: the CPU near flat out, or memory nearly full (Windows starts paging).
    const double CpuLimit = 85, RamLimit = 0.9;

    int range = 2;
    int loadId;
    DateTime loadedAt;

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
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > TimeSpan.FromMinutes(1)) Load(); };
    }

    void Load()
    {
        loadedAt = DateTime.Now;
        var id = ++loadId; // a later pick wins over a slower earlier load
        LoadHistory(id, Ranges[range].Span);
        LoadReports(id, Ranges[range].Span);
    }

    async void LoadHistory(int id, TimeSpan span)
    {
        var ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
        BsonDocument? doc;
        try { doc = await Db.Analysis(span, CpuLimit, ramGb * RamLimit); }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            if (id == loadId) (Status.Text, loadedAt) = ("MongoDB not reachable.", default);
            return;
        }
        if (id != loadId) return;

        static int Count(BsonDocument? d, string facet) => d?[facet].AsBsonArray.FirstOrDefault() is BsonDocument c ? c["n"].ToInt32() : 0;
        var minutes = Count(doc, "minutes");
        var strained = Count(doc, "strained");
        Status.Text = minutes == 0 ? "No data yet: one sample is logged per minute." : $"Based on {Duration(minutes)} of samples";

        var sums = (doc?["apps"].AsBsonArray ?? new BsonArray()).Select(a => (n: a["_id"]["n"].AsString, r: a["_id"]["r"].AsString, v: a["v"].ToDouble())).ToList();
        // An app missing from a minute's top 5 used ~0 then, so averaging over every minute is fair.
        Heaviest.ItemsSource = Kinds.Select(res => new Heavy(res.Title, sums.Where(s => s.r == res.Key).OrderByDescending(s => s.v).Take(5)
            .Select(s => new Busy("", s.n, HistoryPage.Amount(s.r, s.v / minutes))).ToArray())).ToArray();

        StrainTime.Text = Duration(strained);
        StrainSub.Text = minutes == 0 ? "" : $"{strained * 100.0 / minutes:0.#}% of the time: CPU at {CpuLimit}%+ or memory {RamLimit:0%} full."
            + (strained > 0 ? " On top then:" : " Nothing to blame.");
        Culprits.ItemsSource = (doc?["culprits"].AsBsonArray ?? new BsonArray()).Select(c => new Busy(
            c["_id"]["r"].AsString == "ram" ? "Memory" : "CPU", c["_id"]["n"].AsString, Duration(c["m"].ToInt32()))).ToArray();
    }

    async void LoadReports(int id, TimeSpan span)
    {
        List<Report> reports;
        string? error = null;
        try { reports = await Task.Run(() => WinReports.Read(span)); }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            (reports, error) = ([], "Couldn't read the Windows event logs: " + e.Message);
        }
        if (id != loadId) return;
        Reports.ItemsSource = reports;
        ReportsNone.Text = error ?? "Nothing reported: no crashes, freezes, or disk or driver trouble.";
        ReportsNone.Visibility = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Duration(int minutes) => minutes switch
    {
        < 120 => $"{minutes} min",
        < 2880 => $"{minutes / 60.0:0.#} h",
        _ => $"{minutes / 1440.0:0.#} days",
    };
}
