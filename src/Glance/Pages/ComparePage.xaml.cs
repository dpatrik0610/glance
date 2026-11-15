using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Glance.Services;
using Microsoft.Win32;

namespace Glance.Pages;

/// Two files (a line diff), two folders (what's missing or different), or any folders (identical files).
public partial class ComparePage : UserControl
{
    /// A row of the duplicates list: a group's header (File null) or one of its copies.
    public record DupRow(DupGroup Group, DupFile? File)
    {
        public bool IsHeader => File is null;
        public bool IsFile => File is not null;
        public string Title => $"{Group.Files.Count} copies · {Compare.Size(Group.Size)} each · {Compare.Size(Group.Wasted)} reclaimable";
        public string When => $"{File?.Modified:yyyy-MM-dd HH:mm}{(File == Group.Files[0] ? " · oldest" : "")}";
    }

    sealed class Conv(Func<object, object?> f) : IValueConverter
    {
        public object? Convert(object value, Type t, object p, CultureInfo c) => f(value);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    static Brush Tint(uint argb) { var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)); b.Freeze(); return b; }
    static readonly Brush RemovedRow = Tint(0x24FF5A5A), AddedRow = Tint(0x244CC37A), RemovedPart = Tint(0x66FF5A5A), AddedPart = Tint(0x664CC37A);

    public static readonly IValueConverter RowBrush = new Conv(v => ((DiffLine)v).Kind switch { '-' => RemovedRow, '+' => AddedRow, _ => Brushes.Transparent });
    public static readonly IValueConverter Marker = new Conv(v => (char)v switch { '-' => "−", '+' => "+", _ => "" });
    public static readonly IValueConverter KindBrush = new Conv(v => Res((char)v switch { '<' => "Red", '>' => "Green", '≠' => "Amber", _ => "Sub" }));

    string? fileA, fileB, folderA, folderB;
    int run; // newest diff or folder compare; an older one finishing late is dropped
    CancellationTokenSource? busy;
    readonly ObservableCollection<string> roots = [];
    readonly ObservableCollection<DupRow> rows = [];
    List<DupGroup> groups = [];

    public ComparePage()
    {
        InitializeComponent();
        RootList.ItemsSource = roots;
        DupeList.ItemsSource = rows;
        roots.CollectionChanged += (_, _) =>
        {
            RootsHint.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FindButton.IsEnabled = roots.Count > 0;
        };
    }

    void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (FilesView is null) return; // the first chip is checked while the page is still being built
        FilesView.Visibility = FilesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FoldersView.Visibility = FoldersTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DupesView.Visibility = DupesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void DraggedOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    static string[] Dropped(DragEventArgs e, Func<string, bool> keep)
    {
        e.Handled = true;
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? []).Where(keep).ToArray();
    }

    static void Show(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    static void Fail(TextBlock where, Exception e)
    {
        where.Text = e.Message;
        where.Foreground = Res("Red");
    }

    // ===== Files =====

    void FileDropped(object sender, DragEventArgs e)
    {
        var files = Dropped(e, File.Exists);
        if (files.Length >= 2) (fileA, fileB) = (files[0], files[1]);
        else if (files.Length == 1 && sender == FileA) fileA = files[0];
        else if (files.Length == 1) fileB = files[0];
        else return;
        RunFiles();
    }

    void PickFile(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog();
        if (d.ShowDialog() != true) return;
        if ((string)((Button)sender).Tag == "A") fileA = d.FileName; else fileB = d.FileName;
        RunFiles();
    }

    void Swap(object sender, RoutedEventArgs e)
    {
        (fileA, fileB) = (fileB, fileA);
        RunFiles();
    }

    async void RunFiles()
    {
        FileAText.Text = fileA ?? "Left: drop a file, or two at once";
        FileBText.Text = fileB ?? "Right: drop a file";
        FileAText.ToolTip = fileA;
        FileBText.ToolTip = fileB;
        DiffList.ItemsSource = null;
        FileSummary.Inlines.Clear();
        FileSummary.Foreground = Res("Text");
        if (fileA is null || fileB is null) return;
        var (a, b, id) = (fileA, fileB, ++run);
        FileSummary.Text = "Comparing…";
        try
        {
            var first = await Task.Run(() => Compare.FirstDifference(a, b));
            if (id != run) return;
            if (first < 0) { FileSummary.Text = "Identical"; FileSummary.Foreground = Res("Green"); return; }
            var text = await Task.Run(() => Compare.IsText(a) && Compare.IsText(b));
            if (!text || !Shell.OnPath("git.exe"))
            {
                FileSummary.Text = $"Differ from byte {first:N0} on · {Compare.Size(new FileInfo(a).Length)} vs {Compare.Size(new FileInfo(b).Length)}"
                    + (text ? " · install git to see the changed lines" : " · binary");
                return;
            }
            var lines = await Compare.Diff(a, b);
            if (id != run) return;
            int added = lines.Count(l => l.Kind == '+'), removed = lines.Count(l => l.Kind == '-');
            FileSummary.Inlines.Clear();
            if (added + removed == 0) FileSummary.Text = "Only line endings differ";
            else
            {
                FileSummary.Inlines.Add(new Run($"+{added}") { Foreground = Res("Green") });
                FileSummary.Inlines.Add(new Run($"  −{removed}") { Foreground = Res("Red") });
                FileSummary.Inlines.Add(new Run("  lines") { Foreground = Res("Sub") });
            }
            DiffList.ItemsSource = lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { if (id == run) Fail(FileSummary, ex); }
    }

    // The text of a diff line, its changed part marked (Runs can't be bound, so they're filled here).
    void FillLine(object sender, DependencyPropertyChangedEventArgs e)
    {
        var tb = (TextBlock)sender;
        tb.Inlines.Clear();
        if (e.NewValue is not DiffLine line) return;
        if (line.Kind == '@') { tb.Inlines.Add(new Run("⋯") { Foreground = Res("Sub") }); return; }
        var edited = line.Segs.Any(s => s.Kind == ' '); // part of the line kept: mark what changed
        foreach (var seg in line.Segs)
        {
            var r = new Run(seg.Text.Replace("\t", "    "));
            if (edited && seg.Kind != ' ') r.Background = seg.Kind == '-' ? RemovedPart : AddedPart;
            tb.Inlines.Add(r);
        }
    }

    // ===== Folders =====

    void FolderDropped(object sender, DragEventArgs e)
    {
        var dirs = Dropped(e, Directory.Exists);
        if (dirs.Length >= 2) (folderA, folderB) = (dirs[0], dirs[1]);
        else if (dirs.Length == 1 && (string)((FrameworkElement)sender).Tag == "A") folderA = dirs[0];
        else if (dirs.Length == 1) folderB = dirs[0];
        else return;
        RunFolders();
    }

    void PickFolder(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog();
        if (d.ShowDialog() != true) return;
        if ((string)((Button)sender).Tag == "A") folderA = d.FolderName; else folderB = d.FolderName;
        RunFolders();
    }

    async void RunFolders()
    {
        FolderAText.Text = folderA ?? "Left: drop a folder, or two at once";
        FolderBText.Text = folderB ?? "Right: drop a folder";
        FolderList.ItemsSource = null;
        FolderSummary.Foreground = Res("Text");
        FolderSummary.Text = "";
        if (folderA is null || folderB is null) return;
        var (a, b) = (folderA, folderB);
        var cts = Start(FolderCancel);
        FolderSummary.Text = "Comparing…";
        try
        {
            var (items, same) = await Task.Run(() => Compare.Folders(a, b, cts.Token));
            int Count(char k) => items.Count(i => i.Kind == k);
            FolderSummary.Text = items.Count == 0 ? $"Same: all {same:N0} files match"
                : $"{Count('<'):N0} only left · {Count('>'):N0} only right · {Count('≠'):N0} differ · {same:N0} same"
                  + (Count('!') > 0 ? $" · {Count('!')} unreadable" : "");
            if (items.Count == 0) FolderSummary.Foreground = Res("Green");
            FolderList.ItemsSource = items;
        }
        catch (OperationCanceledException) { if (busy == cts) FolderSummary.Text = "Cancelled"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(FolderSummary, ex); }
        finally { Stop(cts, FolderCancel); }
    }

    void DiffItem(object sender, RoutedEventArgs e)
    {
        var item = (FolderItem)((FrameworkElement)sender).DataContext;
        (fileA, fileB) = (item.L!.FullName, item.R!.FullName);
        FilesTab.IsChecked = true;
        RunFiles();
    }

    void ShowItem(object sender, RoutedEventArgs e)
    {
        var item = (FolderItem)((FrameworkElement)sender).DataContext;
        Show((item.L ?? item.R)!.FullName);
    }

    // ===== Long runs: one at a time, cancellable =====

    CancellationTokenSource Start(Button cancel)
    {
        busy?.Cancel();
        cancel.Visibility = Visibility.Visible;
        return busy = new();
    }

    void Stop(CancellationTokenSource cts, Button cancel)
    {
        if (busy != cts) return; // a newer run owns the button now
        cancel.Visibility = Visibility.Collapsed;
        busy = null;
    }

    void Cancel(object sender, RoutedEventArgs e) => busy?.Cancel();

    // ===== Duplicates =====

    void AddRoots(IEnumerable<string> dirs)
    {
        foreach (var d in dirs.Select(Path.GetFullPath))
            if (!roots.Contains(d, StringComparer.OrdinalIgnoreCase)) roots.Add(d);
    }

    void RootDropped(object sender, DragEventArgs e) => AddRoots(Dropped(e, Directory.Exists));

    void AddRoot(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Multiselect = true };
        if (d.ShowDialog() == true) AddRoots(d.FolderNames);
    }

    void RemoveRoot(object sender, RoutedEventArgs e) => roots.Remove((string)((FrameworkElement)sender).DataContext);

    async void FindDupes(object sender, RoutedEventArgs e)
    {
        var cts = Start(DupeCancel);
        FindButton.IsEnabled = false;
        KeepAllButton.Visibility = Visibility.Collapsed;
        DupeSummary.Foreground = Res("Text");
        DupeSummary.Text = "Scanning…";
        groups = [];
        rows.Clear();
        var finished = false;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!finished) DupeSummary.Text = $"Checking {p.Done:N0} of {p.Total:N0} files that share a size…";
        });
        var dirs = roots.ToList();
        try
        {
            groups = await Task.Run(() => Compare.Duplicates(dirs, progress, cts.Token));
            finished = true;
            ShowGroups();
        }
        catch (OperationCanceledException) { finished = true; DupeSummary.Text = "Cancelled"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { finished = true; Fail(DupeSummary, ex); }
        finally
        {
            Stop(cts, DupeCancel);
            FindButton.IsEnabled = roots.Count > 0;
        }
    }

    // Rebuilds the rows from `groups`, dropping sets that are down to one file.
    void ShowGroups(string note = "")
    {
        groups.RemoveAll(g => g.Files.Count < 2);
        rows.Clear();
        foreach (var g in groups)
        {
            rows.Add(new(g, null));
            foreach (var f in g.Files) rows.Add(new(g, f));
        }
        DupeSummary.Text = (groups.Count == 0 ? "No duplicates"
            : $"{groups.Count:N0} sets of identical files · {Compare.Size(groups.Sum(g => g.Wasted))} reclaimable") + note;
        KeepAllButton.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static DupRow RowOf(object sender) => (DupRow)((FrameworkElement)sender).DataContext;

    void ShowDupe(object sender, RoutedEventArgs e) => Show(RowOf(sender).File!.Path);

    async void RecycleDupe(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        await RecycleAll([(row.Group, [row.File!])]);
    }

    async void KeepOldest(object sender, RoutedEventArgs e)
    {
        var g = RowOf(sender).Group;
        await RecycleAll([(g, g.Files.Skip(1).ToList())]);
    }

    async void KeepOldestAll(object sender, RoutedEventArgs e)
    {
        var n = groups.Sum(g => g.Files.Count - 1);
        if (!Dialog.Confirm("Duplicates", $"Move {n:N0} copies to the Recycle Bin?",
                $"The oldest file of each set stays. That frees {Compare.Size(groups.Sum(g => g.Wasted))}.", "Recycle")) return;
        await RecycleAll(groups.Select(g => (g, g.Files.Skip(1).ToList())).ToList());
    }

    // Each copy is checked against a copy that stays, byte for byte, right before it goes: a file edited since the scan
    // isn't a duplicate any more and is left alone. Recycling, not deleting: anything here can be restored.
    async Task RecycleAll(List<(DupGroup Group, List<DupFile> Victims)> work)
    {
        IsEnabled = false;
        int done = 0, skipped = 0;
        try
        {
            foreach (var (group, victims) in work)
                foreach (var v in victims)
                {
                    var keep = group.Files.FirstOrDefault(f => f != v && !victims.Contains(f) && File.Exists(f.Path))
                               ?? group.Files.FirstOrDefault(f => f != v && File.Exists(f.Path));
                    var safe = keep is not null && await Task.Run(() =>
                    {
                        try { return Compare.StillDuplicate(v.Path, keep.Path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
                    });
                    if (!safe) { skipped++; continue; }
                    try
                    {
                        if (!Compare.Recycle(v.Path)) return; // the user said no to Windows' prompt: stop here
                        group.Files.Remove(v);
                        done++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
        }
        finally
        {
            IsEnabled = true;
            if (done > 0) Db.Info($"duplicates: recycled {done}, skipped {skipped}");
            ShowGroups((done > 0 ? $" · {done:N0} recycled" : "") + (skipped > 0 ? $" · {skipped:N0} skipped (changed, missing or in use)" : ""));
        }
    }
}
