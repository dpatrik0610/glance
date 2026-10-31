using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

public partial class SystemPage : UserControl
{
    static readonly Brush GreenBg = Frozen(0x22, 0x4C, 0xC3, 0x7A), AmberBg = Frozen(0x22, 0xF5, 0xB8, 0x3D), RedBg = Frozen(0x22, 0xFF, 0x5A, 0x5A);

    public SystemPage()
    {
        InitializeComponent();
        // Only sample fast while this page is actually on screen.
        IsVisibleChanged += (_, e) => App.Live = (bool)e.NewValue;
        App.Sampled += s => { if (s.Procs.Length > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Show(s)); }; // live samples only
    }

    void Show(Snapshot s)
    {
        CpuLoad.Text = Pct(s.CpuLoad);
        CpuSub.Text = Join(s.CpuClock is { } c ? $"{c / 1000:0.0} GHz" : null, s.CpuPower is { } p ? $"{p:0} W" : null);
        Temp(CpuTempPill, CpuTemp, s.CpuTemp, 70, 85);
        CpuSpark.Push(s.CpuLoad);
        CpuHistory.Push(s.CpuLoad);

        GpuLoad.Text = Pct(s.GpuLoad);
        GpuSub.Text = Join(
            s.VramUsed is { } v ? $"VRAM {v / 1024:0.0}/{s.VramTotal / 1024:0} GB" : null,
            s.GpuPower is { } gp ? $"{gp:0} W" : null,
            s.GpuFan is { } f ? $"{f:0} rpm" : null);
        Temp(GpuTempPill, GpuTemp, s.GpuTemp, 75, 90);
        GpuSpark.Push(s.GpuLoad);
        GpuHistory.Push(s.GpuLoad);

        var ramPct = s.RamUsed / s.RamTotal * 100;
        RamUsed.Text = s.RamUsed is { } r ? $"{r:0.0} GB" : "—";
        RamSub.Text = s.RamTotal is { } t ? $"of {t:0} GB" : "";
        RamPct.Text = Pct(ramPct);
        RamSpark.Push(ramPct);

        NetDown.Text = "↓ " + Rate(s.NetDown);
        NetUp.Text = "↑ " + Rate(s.NetUp);
        DownSpark.Push((float)s.NetDown);
        UpSpark.Push((float)s.NetUp);

        Drives.ItemsSource = s.Drives;
        last = s.Procs;
        ShowProcs();
        CpuName.Text = s.CpuName;
        GpuName.Text = s.GpuName;
        Uptime.Text = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\d\ hh\h\ mm\m");
    }

    Proc[] last = [];
    bool byMemory;

    void ShowProcs()
    {
        if (Procs.IsMouseOver) return; // hold still under the cursor
        Procs.ItemsSource = (byMemory ? last.OrderByDescending(p => p.MemMb) : last.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.MemMb)).Take(7).ToArray();
    }

    void SortCpu(object sender, MouseButtonEventArgs e) => Sort(false);

    void SortMemory(object sender, MouseButtonEventArgs e) => Sort(true);

    void Sort(bool memory)
    {
        byMemory = memory;
        ByCpu.Text = memory ? "CPU" : "CPU ↓";
        ByMem.Text = memory ? "Memory ↓" : "Memory";
        (ByCpu.Foreground, ByMem.Foreground) = memory ? ((Brush)FindResource("Sub"), (Brush)FindResource("Text")) : ((Brush)FindResource("Text"), (Brush)FindResource("Sub"));
        ShowProcs();
    }

    void EndProcess(object sender, RoutedEventArgs e)
    {
        var p = (Proc)((FrameworkElement)sender).DataContext;
        var what = p.Count == 1 ? p.Name : $"all {p.Count} {p.Name} processes";
        if (!Dialog.Confirm("End process", $"End {what}?", "Unsaved work in it is lost.", "End")) return;
        if (HardwareMonitor.Kill(p.Name) is { } error) Dialog.Info("End process", $"Couldn't end all of {p.Name}", error);
        last = last.Where(x => x.Name != p.Name).ToArray();
        Procs.ItemsSource = null; // drop the stale row now, even under the cursor
        Dispatcher.BeginInvoke(ShowProcs, DispatcherPriority.Input);
    }

    static string Pct(float? v) => v is { } x ? $"{x:0}%" : "—";

    static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => p is not null));

    internal static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1e6 => $"{bytesPerSec / 1e6:0.0} MB/s",
        >= 1e3 => $"{bytesPerSec / 1e3:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };

    void Temp(Border pill, TextBlock text, float? t, float warn, float hot)
    {
        if (t is not { } v)
        {
            pill.Visibility = Visibility.Collapsed;
            return;
        }
        pill.Visibility = Visibility.Visible;
        text.Text = $"{v:0}°C";
        (pill.Background, text.Foreground) = v >= hot ? (RedBg, (Brush)FindResource("Red"))
            : v >= warn ? (AmberBg, (Brush)FindResource("Amber"))
            : (GreenBg, (Brush)FindResource("Green"));
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }
}
