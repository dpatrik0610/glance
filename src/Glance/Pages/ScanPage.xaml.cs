using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;
using Microsoft.Win32;

namespace Glance.Pages;

/// Where a drive's or folder's space went: a biggest-first list and a treemap of the same folder. WinDirStat does the
/// scanning when it is installed (DiskScan), Glance's own walk otherwise.
public partial class ScanPage : UserControl
{
    /// Node == null: the "and N more" line at the end.
    public record Row(Node? Node, string Icon, string Name, string Size, double Share);
    public record Kind(string Name, Brush Brush, string Size);

    static readonly Color Accent = ((SolidColorBrush)Application.Current.Resources["Accent"]).Color, Free = Color.FromArgb(0x55, 0x9C, 0x9C, 0xA3), Blue = Color.FromRgb(0x3D, 0xA9, 0xFF);
    static readonly Dictionary<string, Color> KindColors = new()
    {
        ["Video"] = Color.FromRgb(0xFF, 0x5C, 0x8A), ["Images"] = Color.FromRgb(0x3D, 0xA9, 0xFF), ["Audio"] = Color.FromRgb(0x2C, 0xC7, 0xC7),
        ["Archives and disk images"] = Color.FromRgb(0xF2, 0xB3, 0x3D), ["Apps and libraries"] = Color.FromRgb(0xFF, 0x6A, 0x3D),
        ["Documents"] = Color.FromRgb(0x4C, 0xC3, 0x8A), ["Databases and logs"] = Color.FromRgb(0x9B, 0x7B, 0xFF),
        [DiskScan.SmallKind] = Color.FromRgb(0x6A, 0x6A, 0x72), [DiskScan.OtherKind] = Color.FromRgb(0x8A, 0x8F, 0x98),
    };

    // ponytail: the list stops at 200 rows (the rest is one line), so a folder of 50,000 items doesn't build 50,000 rows.
    // Virtualize the list if the tail ever needs to be reachable; the treemap still shows everything.
    const int MaxRows = 200;

    readonly string? winDirStat = DiskScan.WinDirStat();
    string path;
    Node? shown, menuNode;
    CancellationTokenSource? scan;
    readonly TranslateTransform resultShift = new();

    public ScanPage()
    {
        InitializeComponent();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"), Tag = d.Name,
                Content = $"{d.Name.TrimEnd('\\')}  {Compare.Size(d.AvailableFreeSpace)} free", Margin = new Thickness(0, 0, 0, 4),
                ToolTip = $"{d.VolumeLabel} · {Compare.Size(d.AvailableFreeSpace)} free of {Compare.Size(d.TotalSize)}".TrimStart(' ', '·'),
            };
            chip.Checked += (_, _) => Pick(d.Name);
            Drives.Children.Add(chip);
        }
        // Elevated already, or no WinDirStat: there is no faster scan to offer.
        FastButton.Visibility = winDirStat is not null && !Native.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        // Not saved here: a default is only worth writing down once the user picks something.
        if (!Directory.Exists(App.Settings.ScanPath)) App.Settings.ScanPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Pick(path = App.Settings.ScanPath);
        Map.Hovered += n => HoverText.Text = n is null ? "" : Describe(n);
        Map.Entered += Enter;
        Result.RenderTransform = resultShift;
        Say("Pick a drive or a folder, then scan it.");
    }

    static string Describe(Node n) => $"{(n.Lump ? $"{n.Parent!.Path} · files under 1 MB" : n.Path)} · {Compare.Size(n.Size)}";

    void Pick(string what)
    {
        path = what;
        foreach (RadioButton chip in Drives.Children) chip.IsChecked = (string)chip.Tag == what;
        Summary.Text = what;
        DriveRing();
        if (App.Settings.ScanPath == what) return;
        App.Settings.ScanPath = what;
        App.Settings.Save();
    }

    static string Size(double v) => Compare.Size((long)v);

    static DriveInfo? DriveOf(string p)
    {
        try { return new DriveInfo(Path.GetPathRoot(p)!) is { IsReady: true } d ? d : null; }
        catch (ArgumentException) { return null; }
    }

    // The ring at rest: how full the drive of the picked path is.
    void DriveRing()
    {
        if (DriveOf(path) is not { } d) { Ring.Set([]); Ring.Show(0, _ => "", ""); return; }
        Ring.Set([(Accent, d.TotalSize - d.TotalFreeSpace), (Free, d.TotalFreeSpace)]);
        Ring.Show(d.AvailableFreeSpace, Size, $"free of {Compare.Size(d.TotalSize)}");
    }

    void PickFolder(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Folder to scan" };
        if (d.ShowDialog() == true) Pick(d.FolderName);
    }

    void Say(string text)
    {
        Status.Text = text;
        Idle.Visibility = Visibility.Visible;
        Result.Visibility = Visibility.Collapsed;
    }

    void Scan(object sender, RoutedEventArgs e)
    {
        if (scan is null) _ = Run(false);
        else scan.Cancel(); // the button reads "Cancel" during a scan
    }

    void FastScan(object sender, RoutedEventArgs e) => _ = Run(true);

    async Task Run(bool elevated)
    {
        if (scan is not null) return;
        var ct = (scan = new()).Token;
        ScanButton.Content = "Cancel";
        FastButton.IsEnabled = false;
        var watch = Stopwatch.StartNew();
        var by = elevated ? "WinDirStat, fast scan" : "WinDirStat";
        // A whole drive fills the ring as the scan finds its bytes; a folder has nothing to fill, so the comet just circles.
        var drive = DriveOf(path) is { } d && Path.GetFullPath(path).TrimEnd('\\').Length <= 2 ? d.TotalSize - d.TotalFreeSpace : 0;
        Ring.Set([]);
        Ring.Busy = true;
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var progress = new DiskScan.Progress();
        long seen = 0;
        tick.Tick += (_, _) =>
        {
            if (by == "Glance")
            {
                var (files, bytes) = (Interlocked.Read(ref progress.Files), Interlocked.Read(ref progress.Bytes));
                Ring.Show(bytes, Size, $"{files:N0} files");
                if (drive > 0) Ring.Set([(Accent, bytes)], drive);
                Ring.Pull(Accent, (int)Math.Clamp((files - seen) / 400, files > seen ? 1 : 0, 14)); // more found, more sparks
                seen = files;
            }
            else
            {
                Ring.Show(watch.Elapsed.TotalSeconds, v => $"{v:0} s", elevated ? "fast scan" : "WinDirStat scanning");
                Ring.Pull(Blue, 2);
            }
        };
        tick.Start();
        try
        {
            Node? tree = null;
            if (winDirStat is not null)
            {
                Say($"WinDirStat is scanning {path}…");
                tree = await DiskScan.ViaWinDirStat(winDirStat, path, elevated, ct);
                if (tree is null && elevated) { Say("The administrator prompt was declined."); return; }
            }
            if (tree is null) // no WinDirStat, or it gave no report
            {
                by = "Glance";
                Say($"Scanning {path}…");
                tree = await Task.Run(() => DiskScan.Walk(path, progress, ct), ct);
            }
            Summary.Text = $"{tree.Name} · {Compare.Size(tree.Size)} · scanned in {watch.Elapsed.TotalSeconds:0.0} s by {by}";
            Show(tree);
        }
        catch (OperationCanceledException) { Say("Scan cancelled."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Db.Error($"scan {path}", e);
            Say($"Can't scan {path}: {e.Message}");
        }
        finally
        {
            tick.Stop();
            Ring.Busy = false;
            DriveRing();
            scan = null;
            ScanButton.Content = "Scan";
            FastButton.IsEnabled = true;
        }
    }

    internal void Show(Node dir)
    {
        shown = dir;
        PathText.Text = dir.Path;
        PathText.ToolTip = dir.Path;
        UpButton.IsEnabled = dir.Parent is not null;
        var kids = dir.Kids!;
        var top = Math.Max(1, kids.FirstOrDefault()?.Size ?? 1);
        var rows = kids.Take(MaxRows).Select(k => new Row(k, k.IsDir ? "" : k.Lump ? "" : "",
            k.Lump ? "Files under 1 MB" : k.Name, Compare.Size(k.Size), k.Size / (double)top)).ToList();
        if (kids.Count > MaxRows)
            rows.Add(new(null, "", $"and {kids.Count - MaxRows:N0} more", Compare.Size(kids.Skip(MaxRows).Sum(k => k.Size)), 0));
        List.ItemsSource = rows;
        ShowKinds(dir);
        if (kids.Count == 0) { Say("This folder is empty."); return; }
        Idle.Visibility = Visibility.Collapsed;
        if (Result.Visibility != Visibility.Visible)
        {
            Result.Visibility = Visibility.Visible;
            Motion.Rise(Result, resultShift, 14, 320);
        }
        Map.Root = dir; // after it's visible, so its pop-in and zooms play
    }

    void ShowKinds(Node dir)
    {
        var kinds = DiskScan.Kinds(dir);
        KindPanel.Visibility = kinds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        KindBar.Children.Clear();
        KindBar.ColumnDefinitions.Clear();
        for (var i = 0; i < kinds.Count; i++)
        {
            var (name, bytes) = kinds[i];
            KindBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var seg = new Border
            {
                Background = Brush(name), CornerRadius = new CornerRadius(3), Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                ToolTip = $"{name} · {Compare.Size(bytes)} · {bytes * 100.0 / Math.Max(1, dir.Size):0.#}%",
            };
            Grid.SetColumn(seg, i);
            KindBar.Children.Add(seg);
        }
        KindLegend.ItemsSource = kinds.Select(k => new Kind(k.Kind, Brush(k.Kind), Compare.Size(k.Bytes))).ToArray();
        Motion.To(KindGrow, ScaleTransform.ScaleXProperty, 1, 520, 0);
    }

    static Brush Brush(string kind)
    {
        var b = new SolidColorBrush(KindColors.GetValueOrDefault(kind, Colors.Gray));
        b.Freeze();
        return b;
    }

    void Enter(Node n)
    {
        if (n.IsDir && n.Kids!.Count > 0) Show(n);
    }

    void Up(object sender, RoutedEventArgs e)
    {
        if (shown?.Parent is { } p) Show(p);
    }

    static Node? RowNode(object sender) => ((sender as FrameworkElement)?.DataContext as Row)?.Node;

    void RowClick(object sender, MouseButtonEventArgs e)
    {
        if (RowNode(sender) is { } n) Enter(n);
    }

    void RowReveal(object sender, RoutedEventArgs e) => Reveal(RowNode(sender));
    void RowRecycle(object sender, RoutedEventArgs e) => Recycle(RowNode(sender));

    // The menu is about the block it was opened on, wherever the pointer goes next.
    void MapMenuOpening(object sender, ContextMenuEventArgs e)
    {
        menuNode = Map.Hit;
        if (menuNode is null or { Lump: true }) e.Handled = true; // nothing there to act on: no menu
    }

    void MapReveal(object sender, RoutedEventArgs e) => Reveal(menuNode);
    void MapRecycle(object sender, RoutedEventArgs e) => Recycle(menuNode);

    static void Reveal(Node? n)
    {
        if (n is null) return;
        Process.Start("explorer.exe", n.Lump ? $"\"{n.Parent!.Path}\"" : $"/select,\"{n.Path}\"");
    }

    void Recycle(Node? n)
    {
        if (n is null || n.Lump || n.Parent is null || shown is null) return;
        if (!Dialog.Confirm("Scan", $"Move {n.Name} to the Recycle Bin?", $"{n.Path} · {Compare.Size(n.Size)}", "Recycle")) return;
        try
        {
            if (!Compare.Recycle(n.Path)) return; // the user said no to Windows' own prompt
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Scan", $"{n.Name} could not be moved.", e.Message);
            return;
        }
        Map.Vanish(n, () =>
        {
            DiskScan.Remove(n);
            Show(shown);
        });
    }
}
