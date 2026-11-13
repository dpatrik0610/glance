using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// Safe junk (Cleaner.Items), each with what a clean would free. Ticks are remembered; system items ask for admin once.
/// The ring shows the ticked bytes per group; while cleaning, each group's slice drains as its items finish, sparks fly
/// off in its colour, and the middle counts up what was freed.
public partial class CleanPage : UserControl
{
    static readonly Dictionary<string, Color> GroupColors = new()
    {
        ["Windows"] = Color.FromRgb(0xFF, 0x6A, 0x3D), ["Browsers"] = Color.FromRgb(0x3D, 0xA9, 0xFF),
        ["Developer"] = Color.FromRgb(0x9B, 0x7B, 0xFF), ["System"] = Color.FromRgb(0xF2, 0xB3, 0x3D),
    };
    static readonly Color Green = Color.FromRgb(0x4C, 0xC3, 0x7A);

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public sealed class Row(Cleaner.Item item, bool on, Action changed) : INotifyPropertyChanged
    {
        bool on = on;
        public event PropertyChangedEventHandler? PropertyChanged;

        public Cleaner.Item Item => item;
        public string Name => item.Name;
        /// Null while it is being measured.
        public Cleaner.Found? Found { get; private set; }
        public bool Running { get; private set; }
        /// What the last clean took from it; null: not cleaned since it was last measured from scratch.
        public long? Freed { get; private set; }

        public bool On
        {
            get => on;
            set { on = value; PropertyChanged?.Invoke(this, new(nameof(On))); changed(); }
        }

        /// Counts toward the total: ticked, measured, and not held by a running browser.
        public long Bytes => on && !Running ? Found?.Bytes ?? 0 : 0;

        public string SizeText => Found switch
        {
            null => "…",
            { Unreadable: true, Bytes: 0 } => "needs admin",
            var f => Compare.Size(f.Bytes) + (f.Unreadable ? " +" : ""),
        };

        public string Note => Running ? $"{item.Name.Split(' ')[0]} is open: close it to clean this"
            : Freed is { } f ? (f > 0 ? $"✓ Freed {Compare.Size(f)}" : "✓ Cleaned") : item.Note;
        public Brush NoteBrush => Res(Running ? "Amber" : Freed is not null ? "Green" : "Sub");

        public void Set(Cleaner.Found? found, bool running)
        {
            (Found, Running) = (found, running);
            if (found is null) Freed = null;
            PropertyChanged?.Invoke(this, new(null)); // everything shown depends on these
        }

        /// Cleaned: what's left is the estimate less what went (bytes null: the elevated run, which only reports a total).
        public void Cleaned(long? bytes)
        {
            Freed = bytes ?? 0;
            Set(new(bytes is { } b && Found is { } f ? Math.Max(0, f.Bytes - b) : 0), Running);
        }
    }

    public record Section(string Name, Brush Brush, Row[] Rows);
    public record Key(string Name, Brush Brush, string Size);

    readonly Row[] rows;
    readonly string[] groups;
    DateTime measuredAt;
    bool busy;
    string last = ""; // what the last clean did
    // While a clean runs and after it: the ring stays on the scale of what was ticked before, so it shows as drained,
    // and the middle shows what went. Ends at the next tick change or measure.
    double? before;
    long freed;

    static Dictionary<string, bool> Ticks => App.Settings.CleanItems;

    public CleanPage()
    {
        InitializeComponent();
        // Only what this PC has: no rows for browsers and tools that aren't installed.
        rows = Cleaner.Items.Where(i => i.Id == Cleaner.Bin || i.Targets.Any(t => Cleaner.Roots(t).Any()))
            .Select(i => new Row(i, Ticks.TryGetValue(i.Id, out var on) ? on : i.On, Changed)).ToArray();
        foreach (var r in rows) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Row.On)) Remember(r); };
        groups = rows.Select(r => r.Item.Group).Distinct().ToArray();
        Groups.ItemsSource = rows.GroupBy(r => r.Item.Group).Select(g => new Section(
            g.Key == "System" ? "System · asks for administrator rights once" : g.Key, Brush(g.Key), g.ToArray())).ToArray();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - measuredAt > TimeSpan.FromMinutes(2)) _ = MeasureAll(); };
    }

    static Brush Brush(string group)
    {
        var b = new SolidColorBrush(GroupColors.GetValueOrDefault(group, Colors.Gray));
        b.Freeze();
        return b;
    }

    void Remember(Row r)
    {
        before = null;
        if (r.On == r.Item.On) Ticks.Remove(r.Item.Id); else Ticks[r.Item.Id] = r.On;
        App.Settings.Save();
        Changed();
    }

    static string Size(double v) => Compare.Size((long)v);

    void Changed()
    {
        var total = rows.Sum(r => r.Bytes);
        var per = groups.Select(g => (Group: g, Bytes: rows.Where(r => r.Item.Group == g).Sum(r => r.Bytes))).ToArray();
        Ring.Set(per.Select(p => (GroupColors.GetValueOrDefault(p.Group, Colors.Gray), (double)p.Bytes)).ToArray(), before);
        if (before is not null) Ring.Show(freed, Size, "freed", Green);
        else Ring.Show(total, Size, rows.All(r => r.Found is not null) ? "can be freed" : "measuring…");
        Legend.ItemsSource = per.Where(p => rows.Any(r => r.On && r.Item.Group == p.Group))
            .Select(p => new Key(p.Group, Brush(p.Group), Compare.Size(p.Bytes))).ToArray();
        Status.Text = last.Length > 0 ? last : rows.Any(r => r.Running) ? "Open browsers are left out until they're closed." : "";
        CleanButton.IsEnabled = !busy && total > 0;
        MeasureButton.IsEnabled = !busy;
    }

    /// Quiet: after a clean, sizes update in place (no "…", no sweep), so the drained ring stays.
    async Task MeasureAll(bool quiet = false)
    {
        if (busy) return;
        busy = true;
        measuredAt = DateTime.Now;
        if (!quiet)
        {
            before = null;
            foreach (var r in rows) r.Set(null, false);
            Ring.Busy = true;
        }
        Changed();
        await Task.WhenAll(rows.Select(async r =>
        {
            var (found, running) = await Task.Run(() => (Cleaner.Estimate(r.Item), r.Item.Running));
            r.Set(found, running);
            Changed();
        }));
        busy = false;
        Ring.Busy = false;
        Changed();
    }

    async void Measure(object sender, RoutedEventArgs e)
    {
        last = "";
        await MeasureAll();
    }

    void RowClick(object sender, MouseButtonEventArgs e)
    {
        if (busy) return;
        var row = (Row)((FrameworkElement)sender).DataContext;
        row.On = !row.On;
    }

    async void Clean(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picked = rows.Where(r => r.On && !r.Running).ToArray();
        if (picked.Length == 0) return;
        if (!Dialog.Confirm("Clean", $"Delete {Compare.Size(rows.Sum(r => r.Bytes))} for good?",
                string.Join(", ", picked.Select(r => r.Name)) + ". This does not go to the Recycle Bin.", "Clean")) return;
        busy = true;
        before = rows.Sum(r => r.Bytes);
        freed = 0;
        last = "Cleaning…";
        Ring.Busy = true;
        Changed();

        // Elevated Glance cleans everything itself; otherwise the system items go to an elevated copy (one UAC prompt).
        var system = picked.Where(r => r.Item.Admin && !Native.IsAdmin).ToArray();
        var watch = Stopwatch.StartNew();
        var skipped = 0;
        await Task.WhenAll(picked.Except(system).Select(async r =>
        {
            var f = await Task.Run(() => Cleaner.Clean(r.Item));
            freed += f.Bytes;
            skipped += f.Skipped;
            r.Cleaned(f.Bytes);
            Ring.Burst(GroupColors.GetValueOrDefault(r.Item.Group, Colors.Gray), (int)Math.Clamp(f.Bytes >> 26, 4, 70)); // a spark per 64 MB
            Changed();
        }));
        watch.Stop();
        var note = system.Length > 0 ? "" : $" in {watch.Elapsed.TotalSeconds:0.0} s"; // with a UAC prompt in it, the time says nothing
        if (system.Length > 0)
            try
            {
                last = "Cleaning system files…";
                Changed();
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--clean " + string.Join(',', system.Select(r => r.Item.Id)))
                    { UseShellExecute = true, Verb = "runas" })!;
                await p.WaitForExitAsync();
                var mb = Math.Max(0, p.ExitCode); // megabytes freed (Cleaner.Run); negative = it crashed
                freed += (long)mb << 20;
                foreach (var r in system) r.Cleaned(null);
                Ring.Burst(GroupColors["System"], Math.Clamp(mb >> 6, 4, 70));
            }
            catch (Win32Exception) { note = " · system files left: the administrator prompt was declined"; }

        last = $"Freed {Compare.Size(freed)}{note}" + (skipped > 0 ? $" · {skipped:N0} files in use were left" : "");
        Ring.Burst(Green, 50);
        busy = false;
        await MeasureAll(quiet: true);
    }
}
