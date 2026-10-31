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

public class CliTab : Bindable
{
    static int nextId;

    string name = "", color = "";
    bool editing;

    public int Id { get; } = ++nextId;
    public TermPTY Pty { get; } = new();
    public required string Dir { get; init; }
    /// Where the shell is now, from the prompt's OSC 9;9 mark; what a restart reopens in.
    public string Cwd { get; set; } = "";
    public string? Command { get; init; }
    /// A restored Claude tab: picks its last conversation in this folder back up.
    public bool Resume { get; set; }
    public string Name { get => name; set => Set(ref name, value); }
    public bool Editing { get => editing; set => Set(ref editing, value); }
    public string Color { get => color; set { if (Set(ref color, value)) { Raise(nameof(Brush)); Raise(nameof(Tint)); } } }

    public Brush Brush => Solid(0xFF);
    public Brush Tint => Solid(0x2E);

    // Shell output not yet handed to the page; flushed once per dispatcher turn so floods don't post per chunk.
    internal readonly StringBuilder Pending = new();
    internal bool FlushQueued, Starting;

    public bool Exited => Pty.Process?.HasExited == true;

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
public partial class CliPage : UserControl
{
    public static readonly string[] Palette = ["#FF6A3D", "#FF4D5E", "#F5B83D", "#3DDC84", "#5AA9FF", "#B57BFF", "#9C9CA3"];
    public static readonly IValueConverter Leaf = new LeafConverter();

    /// Open terminals, for the exit prompt.
    public static int Running { get; private set; }

    public const string Claude = "claude";

    record Msg(string T, int Id, string? D, int Cols, int Rows, string? Uri);
    record Saved(string Dir, string? Command, string Name, string Color);
    static readonly string SessionFile = Path.Combine(Settings.Dir, "cli-tabs.json");

    // Runs after the profile: wraps whatever prompt it set (Starship or not) to report the folder as OSC 9;9,
    // the Windows Terminal convention. Original prompt first, so it still sees the last command's $?.
    const string CwdHook = """
        $global:GlancePrompt = $function:prompt
        function global:prompt { $p = & $global:GlancePrompt; [Console]::Write("`e]9;9;`"$($PWD.ProviderPath)`"`e\"); $p }
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
        TabList.ItemsSource = tabs;
        Folders.ItemsSource = App.Settings.CliFolders;
        ClaudeButton.Visibility = Shell.OnPath("claude.exe") ? Visibility.Visible : Visibility.Collapsed;
        IsVisibleChanged += (_, _) => FocusTerm();
        new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Reap(), Dispatcher);
        Application.Current.Exit += (_, _) => { Save(); foreach (var t in tabs) Kill(t); };
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
                Open(t.Dir, t.Command, t.Name, t.Color).Resume = t.Command == Claude;
            if (tabs.Count > 0) TabList.SelectedIndex = 0;
        }
        catch (Exception e) when (e is IOException or JsonException) { Db.Warn($"cli restore: {e.Message}"); }
        finally { restoring = false; }
    }

    void Save()
    {
        if (restoring || ending) return;
        try { Settings.WriteAtomic(SessionFile, JsonSerializer.Serialize(tabs.Select(t => new Saved(t.Cwd, t.Command, t.Name, t.Color)))); }
        catch (IOException e) { Db.Warn($"cli save: {e.Message}"); }
    }

    async void InitWeb()
    {
        try
        {
            Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance", "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+F/F5/Ctrl+P stealing keys from the shell
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow; // right-click paste
            };
            core.WebMessageReceived += (_, e) => Receive(JsonSerializer.Deserialize<Msg>(e.WebMessageAsJson, Json)!);
            core.SetVirtualHostNameToFolderMapping("glance.term", Path.Combine(AppContext.BaseDirectory, "Terminal"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://glance.term/term.html");
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
            if (Uri.TryCreate(m.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        if (tabs.FirstOrDefault(t => t.Id == m.Id) is not { } tab) return;
        if (m.T == "in" && tab.Pty.TermProcIsStarted) tab.Pty.WriteToTerm(m.D);
        else if (m.T == "size") Size(tab, m.Cols, m.Rows);
    }

    // The shell starts at the first size the page reports, so it never has to reflow from a guess.
    void Size(CliTab t, int cols, int rows)
    {
        if (t.Starting) { t.Pty.Resize(cols, rows); return; }
        t.Starting = true;
        t.Pty.TerminalOutput += (_, e) => Output(t, e.Data);
        // --continue fails when the folder has no conversation yet; start a fresh one then.
        var run = t.Resume ? $"{Claude} --continue || {Claude}" : t.Command;
        var script = run is null ? CwdHook : CwdHook + "\n" + run;
        var command = $"pwsh.exe -NoLogo -NoExit -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}";
        new Thread(() => // Start blocks for the shell's lifetime, pumping its output
        {
            try { t.Pty.Start(command, cols, rows, false, null, t.Dir); }
            catch (Exception e) { Db.Error($"cli start {t.Name}", e); }
        }) { IsBackground = true, Name = "cli " + t.Id }.Start();
    }

    void Output(CliTab t, string data)
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

    public CliTab Open(string? dir = null, string? command = null, string? name = null, string? color = null)
    {
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tab = new CliTab
        {
            Dir = dir,
            Command = command,
            Name = name ?? LeafOf(dir),
            Color = color ?? Palette[tabs.Count % Palette.Length],
            Cwd = dir,
        };
        Post(new { t = "open", id = tab.Id });
        tabs.Add(tab);
        Running = tabs.Count;
        TabList.SelectedItem = tab;
        Save();
        return tab;
    }

    void Close(CliTab t)
    {
        Kill(t);
        Post(new { t = "close", id = t.Id });
        var i = tabs.IndexOf(t);
        tabs.Remove(t);
        Running = tabs.Count;
        if (tabs.Count > 0) TabList.SelectedIndex = Math.Min(i, tabs.Count - 1);
        Save();
    }

    // Whole tree: a dev server started from the shell goes with it.
    static void Kill(CliTab t)
    {
        try { if (t.Pty.Process is { HasExited: false } p) p.Kill(true); }
        catch (Exception e) { Db.Warn($"cli kill {t.Name}: {e.Message}"); }
    }

    // A shell that exited on its own (`exit`) closes its tab.
    void Reap()
    {
        foreach (var t in tabs.Where(t => t.Exited).ToList()) Close(t);
    }

    void FocusTerm() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsVisible || Current is not { Editing: false }) return;
        Web.Focus();
        Post(new { t = "focus" });
    }, DispatcherPriority.Input);

    void TabSelected(object sender, SelectionChangedEventArgs e)
    {
        Screen.BorderBrush = Current?.Brush ?? (Brush)FindResource("Line");
        Empty.Visibility = tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = tabs.Count == 0 ? Visibility.Hidden : Visibility.Visible;
        if (Current is { } t) Post(new { t = "show", id = t.Id });
        FocusTerm();
    }

    void NewShell(object sender, RoutedEventArgs e) => Open(Current?.Dir);

    void NewClaude(object sender, RoutedEventArgs e) => Open(Current?.Dir, Claude, "Claude", Palette[0]);

    static CliTab TabOf(object sender) => (CliTab)((FrameworkElement)sender).DataContext;

    void CloseTab(object sender, RoutedEventArgs e) => Close(TabOf(sender));

    void Duplicate(object sender, RoutedEventArgs e)
    {
        var t = TabOf(sender);
        Open(t.Cwd, t.Command, t.Name, t.Color);
    }

    // Double-click a tab to rename; single click puts the cursor back in its terminal.
    void TabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { TabOf(sender).Editing = true; e.Handled = true; }
        else FocusTerm();
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
        if (t.Name.Trim().Length == 0) t.Name = LeafOf(t.Dir);
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
        if (Current?.Pty is not { TermProcIsStarted: true } pty) { Open(path); return; }
        pty.WriteToTerm($"Set-Location -LiteralPath '{path.Replace("'", "''")}'\r");
        FocusTerm();
    }

    void FolderNewTab(object sender, RoutedEventArgs e) => Open(FolderOf(sender));

    void FolderClaude(object sender, RoutedEventArgs e) => Open(FolderOf(sender), Claude, "Claude", Palette[0]);

    void FolderExplorer(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{FolderOf(sender)}\"");

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
        FolderPanel.Visibility = FoldersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    static string LeafOf(string dir) => Path.GetFileName(dir.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : dir;

    sealed class LeafConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => LeafOf((string)value);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
