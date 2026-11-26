using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

public partial class SystemPage : UserControl, IDisposable, IHeader
{
    public FrameworkElement Header { get; }
    public FrameworkElement State { get; } = new TextBlock { Style = Skin.Style("Label") };

    /// A table row: the process plus how it looks.
    public record Row(Proc P, string Initial, string Sub, string? Status, Brush? StatusBrush, Brush CpuBrush, string DiskText);

    static readonly Brush GreenBg = Frozen(0x22, 0x4C, 0xC3, 0x7A), AmberBg = Frozen(0x22, 0xF5, 0xB8, 0x3D), RedBg = Frozen(0x22, 0xFF, 0x5A, 0x5A);

    public SystemPage()
    {
        InitializeComponent();
        Header = MainWindow.Lift(Bar);
        var accent = Skin.BrushColor("Accent");
        foreach (var spark in new[] { CpuSpark, GpuSpark, DownSpark, UpSpark }) spark.Color = accent;
        // Only sample fast while this page is actually on screen.
        IsVisibleChanged += (_, e) => App.Live = (bool)e.NewValue;
        App.Sampled += Sampled;
    }

    void Sampled(Snapshot s) { if (s.Procs.Length > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Show(s)); } // live samples only

    public void Dispose() => App.Sampled -= Sampled;

    void Show(Snapshot s)
    {
        CpuLoad.Text = Num(s.CpuLoad);
        CpuSub.Text = Join(s.CpuClock is { } c ? $"{c / 1000:0.0} GHz" : null, s.CpuPower is { } p ? $"{p:0} W" : null);
        Temp(CpuTempPill, CpuTemp, s.CpuTemp, 70, 85);
        CpuSpark.Push(s.CpuLoad);

        GpuLoad.Text = Num(s.GpuLoad);
        GpuSub.Text = Join(
            s.VramUsed is { } v ? $"VRAM {v / 1024:0.0}/{s.VramTotal / 1024:0} GB" : null,
            s.GpuPower is { } gp ? $"{gp:0} W" : null);
        Temp(GpuTempPill, GpuTemp, s.GpuTemp, 75, 90);
        GpuSpark.Push(s.GpuLoad);

        var down = Units.Rate(s.NetDown).Split(' ', 2);
        NetDown.Text = down[0];
        NetDownUnit.Text = (down.Length > 1 ? down[1] : "") + " down";
        NetUp.Text = "↑ " + Units.Rate(s.NetUp);
        DownSpark.Push((float)s.NetDown);
        UpSpark.Push((float)s.NetUp);

        var ramPct = s.RamUsed / s.RamTotal * 100;
        RamUsed.Text = s.RamUsed is { } r ? $"{r:0.0}" : "—";
        RamSub.Text = s.RamTotal is { } t ? $"/ {t:0} GB" : "";
        RamPct.Text = ramPct is { } rp ? $"{rp:0}%" : "";
        RamBar.Value = ramPct ?? 0;
        RamTop.Text = s.Procs.MaxBy(p => p.MemMb) is { } top ? $"Top: {top.Name} · {top.MemText}" : "";

        // The Windows drive
        var sys = Path.GetPathRoot(Environment.SystemDirectory)![..1];
        if ((s.Drives.FirstOrDefault(d => d.Name.StartsWith(sys, StringComparison.OrdinalIgnoreCase)) ?? s.Drives.FirstOrDefault()) is { } drive)
        {
            DiskName.Text = $"DISK {drive.Name.TrimEnd('\\')}";
            DiskFree.Text = $"{drive.TotalGb - drive.UsedGb:0.0}";
            DiskPct.Text = $"{drive.Pct:0}% used";
            DiskBar.Foreground = Skin.Brush(drive.Pct >= 90 ? "Amber" : "Accent");
            DiskPct.Foreground = Skin.Brush(drive.Pct >= 90 ? "Amber" : "Sub");
            DiskBar.Value = drive.Pct;
            DiskSub.Text = $"of {drive.TotalGb:0} GB" + (s.Drives.Length > 1 ? $" · {s.Drives.Length - 1} more on Disk" : "");
        }

        last = s.Procs;
        ShowProcs();
        CpuTile.ToolTip = $"{s.CpuName}\nOpen in History";
        GpuTile.ToolTip = $"{s.GpuName}\nOpen in History";

        var busy = s.CpuLoad >= 85 || ramPct >= 90 || s.CpuTemp >= 85 || s.GpuTemp >= 90;
        var state = (TextBlock)State;
        state.Inlines.Clear();
        state.Inlines.Add(new System.Windows.Documents.Run(busy ? "Busy" : "Healthy") { Foreground = Skin.Brush(busy ? "Amber" : "Green") });
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        state.Inlines.Add($" · {s.Procs.Sum(p => p.Count)} processes · up " + (up.Days > 0 ? $"{up.Days} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min"));
    }

    Proc[] last = [];
    string sortBy = "CPU"; // a column header's Tag

    static double Key(Proc p, string by) => by switch { "GPU" => p.Gpu, "Memory" => p.MemMb, "Disk" => p.Disk, _ => p.Cpu };

    static bool Hot(Proc p) => p.Cpu >= 5 || p.Gpu >= 10 || p.MemMb >= 1024 || p.Disk >= 1e6;

    Row ToRow(Proc p)
    {
        // Only what's off gets a status; a normal running process shows none
        var status = p.Cpu >= 10 ? "High CPU" : p.MemMb >= 2048 ? "High memory" : null;
        return new Row(p, p.Name[..1].ToUpperInvariant(), p.Count == 1 ? "1 process" : $"{p.Count} processes", status, status == null ? null : Skin.Brush("Amber"),
            Skin.Brush(p.Cpu >= 10 ? "Amber" : "Accent"), Units.Rate(p.Disk));
    }

    void ShowProcs()
    {
        var hot = last.Count(Hot);
        AllCount.Text = $" {last.Length}";
        HotCount.Text = $" {hot}";
        if (Procs.IsMouseOver) return; // hold still under the cursor
        Procs.ItemsSource = (HotChip.IsChecked == true ? last.Where(Hot) : last)
            .OrderByDescending(p => Key(p, sortBy)).ThenByDescending(p => p.MemMb).Take(60).Select(ToRow).ToArray();
    }

    void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ShowProcs();
    }

    void SortBy(object sender, MouseButtonEventArgs e)
    {
        sortBy = (string)((FrameworkElement)sender).Tag;
        foreach (var h in new[] { ByCpu, ByGpu, ByMem, ByDisk })
        {
            var on = (string)h.Tag == sortBy;
            h.Text = on ? h.Tag + " ↓" : (string)h.Tag;
            h.Foreground = Skin.Brush(on ? "Text" : "Sub");
        }
        ShowProcs();
    }

    void EndProcess(object sender, RoutedEventArgs e)
    {
        var p = ((Row)((FrameworkElement)sender).DataContext).P;
        var what = p.Count == 1 ? p.Name : $"all {p.Count} {p.Name} processes";
        if (!Dialog.Confirm("End process", $"End {what}?", "Unsaved work in it is lost.", "End")) return;
        if (HardwareMonitor.Kill(p.Name) is { } error) Dialog.Info("End process", $"Couldn't end all of {p.Name}", error);
        last = last.Where(x => x.Name != p.Name).ToArray();
        Procs.ItemsSource = null; // drop the stale row now, even under the cursor
        Dispatcher.BeginInvoke(ShowProcs, DispatcherPriority.Input);
    }

    // Tiles (and their temperature pills) open their chart on the History page.
    void OpenHistory(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return; // a pill inside a tile already handled it
        e.Handled = true;
        if (((MainWindow)Window.GetWindow(this)).ShowTab("History") is HistoryPage h) h.Spotlight((string)((FrameworkElement)sender).Tag);
    }

    static string Num(float? v) => v is { } x ? $"{x:0}" : "—";

    static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => p is not null));

    void Temp(Border pill, TextBlock text, float? t, float warn, float hot)
    {
        if (t is not { } v)
        {
            pill.Visibility = Visibility.Collapsed;
            return;
        }
        pill.Visibility = Visibility.Visible;
        text.Text = $"{v:0}°C";
        (pill.Background, text.Foreground) = v >= hot ? (RedBg, Skin.Brush("Red"))
            : v >= warn ? (AmberBg, Skin.Brush("Amber"))
            : (GreenBg, Skin.Brush("Green"));
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }
}
