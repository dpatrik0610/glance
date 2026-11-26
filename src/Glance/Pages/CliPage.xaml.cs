using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Glance.Services;
using Microsoft.Web.WebView2.Core;

namespace Glance.Pages;

/// One shell. Started at the first size its pane reports; lives as long as Glance unless closed or exited.
public class CliPane
{
    static int nextId;

    public int Id { get; } = ++nextId;
    public TermPTY Pty { get; } = new();
    public required string Dir { get; init; }
    /// Where the shell is now, from the prompt's OSC 9;9 mark; what a restart reopens in.
    public string Cwd { get; set; } = "";
    public string? Command { get; init; }
    /// A restored Claude pane: picks its last conversation in this folder back up.
    public bool Resume { get; init; }

    // Shell output not yet handed to the page; flushed once per dispatcher turn so floods don't post per chunk.
    internal readonly StringBuilder Pending = new();
    internal bool FlushQueued, Starting;

    public bool Exited => Pty.Process?.HasExited == true;
}

/// A tab's layout: one pane, or two nodes side by side (stacked when Column), A taking Ratio of the room.
public class CliNode
{
    static int nextId;

    public int Id { get; } = ++nextId;
    public CliPane? Pane { get; set; }
    public bool Column { get; set; }
    public double Ratio { get; set; } = .5;
    public CliNode? A { get; set; }
    public CliNode? B { get; set; }

    public IEnumerable<CliNode> All() => Pane is not null ? [this] : [this, .. A!.All(), .. B!.All()];

    /// Puts `added` next to `at` on the given side (left, right, up, down).
    public void Split(CliPane at, CliPane added, string side)
    {
        var leaf = All().First(n => n.Pane == at);
        CliNode old = new() { Pane = at }, add = new() { Pane = added };
        (leaf.Pane, leaf.Column, leaf.Ratio) = (null, side is "up" or "down", .5);
        (leaf.A, leaf.B) = side is "left" or "up" ? (add, old) : (old, add);
    }

    /// This tree without the pane; its split collapses into the other side. Null when it was the only pane.
    public CliNode? Without(CliPane p)
    {
        if (Pane is not null) return Pane == p ? null : this;
        var (a, b) = (A!.Without(p), B!.Without(p));
        if (a is null) return b;
        if (b is null) return a;
        (A, B) = (a, b);
        return this;
    }
}

/// A tab in the strip: a tree of panes split any way, any number of times.
public class CliTab : Bindable
{
    static int nextId;

    string name = "", color = "";
    bool editing;

    public int Id { get; } = ++nextId;
    public required CliNode Root { get; set; }
    /// The pane keys go to; folder clicks and splits act on it.
    public CliPane Focus { get; set; } = null!;
    public IEnumerable<CliPane> Panes => Root.All().Select(n => n.Pane).OfType<CliPane>();
    public string Name { get => name; set => Set(ref name, value); }
    public bool Editing { get => editing; set => Set(ref editing, value); }
    public string Color { get => color; set { if (Set(ref color, value)) { Raise(nameof(Brush)); Raise(nameof(Tint)); } } }

    public Brush Brush => Solid(0xFF);
    public Brush Tint => Solid(0x2E);

    Brush Solid(byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(color);
        var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// Terminal tabs: shells run on ConPTY here; xterm.js (Terminal\term.html) in one transparent WebView2 draws them.
/// Shells live as long as Glance does: switching pages or hiding to the tray only hides them, so servers keep running.
public partial class CliPage : UserControl, IHeader
{
    public FrameworkElement Header => Strip;
    public bool OwnsTitleRow => true; // the tab strip sits in the window's title row, no page header

    public static readonly string[] Palette = ["#FF6A3D", "#FF4D5E", "#F5B83D", "#3DDC84", "#5AA9FF", "#B57BFF", "#9C9CA3"];
    public static readonly IValueConverter Leaf = new LeafConverter();

    /// Open terminals, for the exit prompt.
    public static int Running { get; private set; }

    public const string Claude = "claude";

    record Msg(string T, int Id, string? D, int Cols, int Rows, string? Uri, double R);
    // A saved node: a pane (Dir, Command) or a split (Column, Ratio, A, B). A tab's root also carries Name and Color,
    // which keeps files from before splits (one pane per tab) loading as they are.
    record Saved(string? Dir, string? Command, bool Column = false, double Ratio = .5, Saved? A = null, Saved? B = null,
        string? Name = null, string? Color = null);
    static readonly string SessionFile = Path.Combine(Settings.Dir, "cli-tabs.json");

    // Runs after the profile: wraps whatever prompt it set (Starship or not) to report the folder as OSC 9;9,
    // the Windows Terminal convention. Original prompt first, so it still sees the last command's $?.
    const string CwdHook = """
        $global:GlancePrompt = $function:prompt
        function global:prompt { $p = & $global:GlancePrompt; [Console]::Write("$([char]27)]9;9;`"$($PWD.ProviderPath)`"$([char]27)\"); $p }
        """;

    [GeneratedRegex(@"\x1b\]9;9;""?([^""\x07\x1b]+)""?(?:\x07|\x1b\\)")]
    internal static partial Regex CwdMark();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly ObservableCollection<CliTab> tabs = [];
    readonly List<object> queued = []; // messages sent before the page loaded
    bool ready, restoring, ending;

    public CliPage()
    {
        InitializeComponent();
        MainWindow.Lift(Strip);
        TabList.ItemsSource = tabs;
        Folders.ItemsSource = App.Settings.CliFolders;
        ClaudeItem.Visibility = Shell.OnPath("claude.exe") ? Visibility.Visible : Visibility.Collapsed;
        IsVisibleChanged += (_, _) => FocusTerm();
        new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Reap(), Dispatcher);
        Application.Current.Exit += (_, _) => { Save(); foreach (var p in Panes) Kill(p); };
        // Windows may kill the shells before Glance at shutdown; don't let Reap save that as "no tabs".
        Application.Current.SessionEnding += (_, _) => { Save(); ending = true; };
        TabSelected(this, null!);
        InitWeb();
        Restore();
    }

    // Tabs come back after an exit or reboot: same names, colors and commands, in the folder each shell was last in.
    void Restore()
    {
        try
        {
            if (!File.Exists(SessionFile)) return;
            restoring = true;
            foreach (var t in JsonSerializer.Deserialize<Saved[]>(File.ReadAllText(SessionFile)) ?? [])
                Add(Build(t, true), t.Name, t.Color);
            if (tabs.Count > 0) TabList.SelectedIndex = 0;
        }
        catch (Exception e) when (e is IOException or JsonException) { Db.Warn($"cli restore: {e.Message}"); }
        finally { restoring = false; }
    }

    void Save()
    {
        if (restoring || ending) return;
        try { Settings.WriteAtomic(SessionFile, JsonSerializer.Serialize(tabs.Select(t => Snapshot(t.Root) with { Name = t.Name, Color = t.Color }))); }
        catch (IOException e) { Db.Warn($"cli save: {e.Message}"); }
    }

    static Saved Snapshot(CliNode n) => n.Pane is { } p
        ? new(p.Cwd, p.Command)
        : new(null, null, n.Column, n.Ratio, Snapshot(n.A!), Snapshot(n.B!));

    static CliNode Build(Saved s, bool resume) => s is { A: { } a, B: { } b }
        ? new() { Column = s.Column, Ratio = Math.Clamp(s.Ratio, .05, .95), A = Build(a, resume), B = Build(b, resume) }
        : new() { Pane = NewPane(s.Dir, s.Command, resume && s.Command == Claude) };

    async void InitWeb()
    {
        try
        {
            await WebHost.Open(Web, "glance.term", "Terminal", "term.html", core =>
            {
                core.PermissionRequested += (_, e) =>
                {
                    if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow; // right-click paste
                };
                core.WebMessageReceived += (_, e) => Receive(JsonSerializer.Deserialize<Msg>(e.WebMessageAsJson, Json)!);
            });
        }
        catch (Exception e) { Db.Error("cli webview", e); } // e.g. WebView2 runtime missing
    }

    void Post(object message)
    {
        if (!ready) { queued.Add(message); return; }
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    void Receive(Msg m)
    {
        if (m.T == "ready")
        {
            ready = true;
            foreach (var q in queued) Post(q);
            queued.Clear();
            return;
        }
        if (m.T == "palette") // Ctrl+K inside a terminal
        {
            ((MainWindow)Window.GetWindow(this)).OpenPalette();
            return;
        }
        if (m.T == "link")
        {
            Shell.OpenLink(m.Uri);
            return;
        }
        if (m.T == "ratio") // a divider was dragged
        {
            if (tabs.SelectMany(t => t.Root.All()).FirstOrDefault(n => n.Id == m.Id) is { } node) node.Ratio = Math.Clamp(m.R, .05, .95);
            Save();
            return;
        }
        if (Panes.FirstOrDefault(p => p.Id == m.Id) is not { } pane) return;
        switch (m.T)
        {
            case "in" when pane.Pty.TermProcIsStarted: pane.Pty.WriteToTerm(m.D); break;
            case "size": Size(pane, m.Cols, m.Rows); break;
            case "active": TabOf(pane)!.Focus = pane; break;
            case "split": Split(pane, m.D ?? "right"); break;
            case "closepane": ClosePane(pane); break;
        }
    }

    // The shell starts at the first size the page reports, so it never has to reflow from a guess.
    void Size(CliPane t, int cols, int rows)
    {
        if (t.Starting) { t.Pty.Resize(cols, rows); return; }
        t.Starting = true;
        t.Pty.TerminalOutput += (_, e) => Output(t, e.Data);
        // --continue fails when the folder has no conversation yet; start a fresh one then.
        // Claude tabs go through caveman's layer when it's installed (it forwards the arguments and the exit code).
        var claude = t.Command == Claude && Shell.OnPath("caveman.cmd") ? "caveman claude" : Claude;
        var run = t.Resume ? $"{claude} --continue; if (-not $?) {{ {claude} }}" : t.Command == Claude ? claude : t.Command;
        var script = run is null ? CwdHook : CwdHook + "\n" + run;
        var command = $"{Shell.PowerShell} -NoLogo -NoExit -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}";
        new Thread(() => // Start blocks for the shell's lifetime, pumping its output
        {
            try { t.Pty.Start(command, cols, rows, false, null, t.Dir); }
            catch (Exception e) { Db.Error($"cli start {t.Dir}", e); }
        }) { IsBackground = true, Name = "cli " + t.Id }.Start();
    }

    void Output(CliPane t, string data)
    {
        // ponytail: a mark split across two output chunks is missed; the next prompt catches up.
        if (data.Contains("\x1b]9;9;") && CwdMark().Matches(data) is { Count: > 0 } marks && marks[^1].Groups[1].Value is var cwd && cwd != t.Cwd)
        {
            t.Cwd = cwd;
            Dispatcher.BeginInvoke(Save);
        }
        lock (t.Pending)
        {
            t.Pending.Append(data);
            if (t.FlushQueued) return;
            t.FlushQueued = true;
        }
        Dispatcher.BeginInvoke(() =>
        {
            string d;
            lock (t.Pending)
            {
                d = t.Pending.ToString();
                t.Pending.Clear();
                t.FlushQueued = false;
            }
            Post(new { t = "out", id = t.Id, d });
        });
    }

    CliTab? Current => TabList.SelectedItem as CliTab;

    IEnumerable<CliPane> Panes => tabs.SelectMany(t => t.Panes);

    CliTab? TabOf(CliPane p) => tabs.FirstOrDefault(t => t.Panes.Contains(p));

    static CliPane NewPane(string? dir, string? command, bool resume = false)
    {
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new() { Dir = dir, Cwd = dir, Command = command, Resume = resume };
    }

    public CliTab Open(string? dir = null, string? command = null, string? name = null, string? color = null)
    {
        var pane = NewPane(dir, command);
        return Add(new() { Pane = pane }, name ?? LeafOf(pane.Dir), color);
    }

    CliTab Add(CliNode root, string? name, string? color)
    {
        var tab = new CliTab { Root = root, Color = color ?? Palette[tabs.Count % Palette.Length] };
        tab.Focus = tab.Panes.First();
        tab.Name = name ?? LeafOf(tab.Focus.Dir);
        tabs.Add(tab);
        Running = Panes.Count();
        TabList.SelectedItem = tab;
        Save();
        return tab;
    }

    void Close(CliTab t)
    {
        foreach (var p in t.Panes)
        {
            Kill(p);
            Post(new { t = "close", id = p.Id });
        }
        var i = tabs.IndexOf(t);
        tabs.Remove(t);
        Running = Panes.Count();
        if (tabs.Count > 0) TabList.SelectedIndex = Math.Min(i, tabs.Count - 1);
        Save();
    }

    /// A new shell beside the pane, in the folder that pane is in now.
    void Split(CliPane at, string side)
    {
        if (TabOf(at) is not { } tab) return;
        var added = NewPane(at.Cwd, null);
        tab.Root.Split(at, added, side);
        tab.Focus = added;
        Running = Panes.Count();
        Save();
        if (tab == Current) TabSelected(this, null!);
    }

    void ClosePane(CliPane p)
    {
        if (TabOf(p) is not { } tab) return;
        if (tab.Root.Without(p) is not { } rest) { Close(tab); return; } // its last pane
        Kill(p);
        Post(new { t = "close", id = p.Id });
        tab.Root = rest;
        if (tab.Focus == p) tab.Focus = tab.Panes.First();
        Running = Panes.Count();
        Save();
        if (tab == Current) TabSelected(this, null!);
    }

    // Whole tree: a dev server started from the shell goes with it.
    static void Kill(CliPane t)
    {
        try { if (t.Pty.Process is { HasExited: false } p) p.Kill(true); }
        catch (Exception e) { Db.Warn($"cli kill {t.Dir}: {e.Message}"); }
    }

    // A shell that exited on its own (`exit`) closes its pane, and the tab with its last one.
    void Reap()
    {
        foreach (var p in Panes.Where(p => p.Exited).ToList()) ClosePane(p);
    }

    void FocusTerm() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsVisible || Current is not { Editing: false }) return;
        Web.Focus();
        Post(new { t = "focus" });
    }, DispatcherPriority.Input);

    void TabSelected(object sender, SelectionChangedEventArgs e)
    {
        Screen.BorderBrush = Current?.Brush ?? Skin.Brush("Line");
        Empty.Visibility = tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = tabs.Count == 0 ? Visibility.Hidden : Visibility.Visible;
        if (Current is { } t) Post(new { t = "show", tree = View(t.Root), focus = t.Focus.Id });
        FocusTerm();
    }

    // The layout as term.html draws it: {id} for a pane, {node, col, r, a, b} for a split.
    static object View(CliNode n) => n.Pane is { } p
        ? new { id = p.Id }
        : new { node = n.Id, col = n.Column, r = n.Ratio, a = View(n.A!), b = View(n.B!) };

    void NewShell(object sender, RoutedEventArgs e) => Open(Current?.Focus.Dir);

    void NewClaude(object sender, RoutedEventArgs e) => Open(Current?.Focus.Dir, Claude, "Claude", Palette[0]);

    static CliTab TabOf(object sender) => (CliTab)((FrameworkElement)sender).DataContext;

    void CloseTab(object sender, RoutedEventArgs e) => Close(TabOf(sender));

    // The whole layout again, each pane in the folder it's in now.
    void Duplicate(object sender, RoutedEventArgs e)
    {
        var t = TabOf(sender);
        Add(Build(Snapshot(t.Root), false), t.Name, t.Color);
    }

    // Tab menu items act on that tab's focused pane; the toolbar's on the current tab's.
    void SplitMenu(object sender, RoutedEventArgs e)
    {
        if ((((FrameworkElement)sender).DataContext as CliTab ?? Current) is { } t) Split(t.Focus, (string)((FrameworkElement)sender).Tag);
    }

    void ClosePaneMenu(object sender, RoutedEventArgs e)
    {
        if ((((FrameworkElement)sender).DataContext as CliTab ?? Current) is { } t) ClosePane(t.Focus);
    }

    // Double-click a tab to rename; single click puts the cursor back in its terminal (or starts a drag).
    void TabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { TabOf(sender).Editing = true; e.Handled = true; return; }
        (dragging, dragStart) = (TabOf(sender), e.GetPosition(TabList));
        FocusTerm();
    }

    // Drag a tab sideways to reorder; the strip rearranges live under the cursor.
    CliTab? dragging;
    Point dragStart;

    void TabDragMove(object sender, MouseEventArgs e)
    {
        if (dragging is not { } t || e.LeftButton != MouseButtonState.Pressed) { dragging = null; return; }
        if (Math.Abs(e.GetPosition(TabList).X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        dragging = null;
        DragDrop.DoDragDrop(TabList, new DataObject(typeof(CliTab), t), DragDropEffects.Move);
        Save();
        FocusTerm();
    }

    void TabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (e.Data.GetData(typeof(CliTab)) is not CliTab t
            || ItemsControl.ContainerFromElement(TabList, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: CliTab over } item
            || over == t) return;
        int from = tabs.IndexOf(t), to = tabs.IndexOf(over);
        // Only swap once the cursor is past the neighbor's middle, so tabs of different widths don't flip back and forth.
        var x = e.GetPosition(item).X;
        if (to > from ? x < item.ActualWidth / 2 : x > item.ActualWidth / 2) return;
        tabs.Move(from, to);
    }

    // The strip scrolls sideways with the wheel once tabs overflow.
    void TabWheel(object sender, MouseWheelEventArgs e)
    {
        if (VisualTreeHelper.GetChildrenCount(TabList) == 0 || VisualTreeHelper.GetChild(TabList, 0) is not Border { Child: ScrollViewer sv }) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    void Rename(object sender, RoutedEventArgs e) => TabOf(sender).Editing = true;

    void EditShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box) return;
        Window.GetWindow(box)?.Activate(); // the terminal window may hold focus
        box.Focus();
        box.SelectAll();
    }

    void EditKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        TabOf(sender).Editing = false;
        FocusTerm();
    }

    void EditDone(object sender, KeyboardFocusChangedEventArgs e)
    {
        var t = TabOf(sender);
        if (t.Name.Trim().Length == 0) t.Name = LeafOf(t.Focus.Dir);
        t.Editing = false;
        Save();
    }

    // Swatches sit inside the menu header, whose DataContext is the color; the tab is the menu's.
    void SetColor(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = (DependencyObject)sender;
        while (d is not (null or System.Windows.Controls.ContextMenu)) d = VisualTreeHelper.GetParent(d);
        if (d is not System.Windows.Controls.ContextMenu { DataContext: CliTab t } menu) return;
        t.Color = (string)((Button)sender).Tag;
        menu.IsOpen = false;
        Save();
        TabSelected(this, null!);
    }

    static string FolderOf(object sender) => (string)((FrameworkElement)sender).DataContext;

    void GoFolder(object sender, RoutedEventArgs e)
    {
        var path = FolderOf(sender);
        if (Current?.Focus.Pty is not { TermProcIsStarted: true } pty) { Open(path); return; }
        pty.WriteToTerm($"Set-Location -LiteralPath '{path.Replace("'", "''")}'\r");
        FocusTerm();
    }

    void FolderNewTab(object sender, RoutedEventArgs e) => Open(FolderOf(sender));

    void FolderClaude(object sender, RoutedEventArgs e) => Open(FolderOf(sender), Claude, "Claude", Palette[0]);

    void FolderExplorer(object sender, RoutedEventArgs e) => Shell.Open(FolderOf(sender));

    void FolderRemove(object sender, RoutedEventArgs e)
    {
        App.Settings.CliFolders.Remove(FolderOf(sender));
        App.ApplySettings();
        Folders.Items.Refresh();
    }

    void AddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a quick folder" };
        if (dialog.ShowDialog() != true || App.Settings.CliFolders.Contains(dialog.FolderName)) return;
        App.Settings.CliFolders.Add(dialog.FolderName);
        App.ApplySettings();
        Folders.Items.Refresh();
    }

    void ToggleFolders(object sender, RoutedEventArgs e) =>
        FolderPanel.Visibility = FoldersItem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    static string LeafOf(string dir) => Path.GetFileName(dir.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : dir;

    sealed class LeafConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => LeafOf((string)value);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
