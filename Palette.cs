using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Pages;
using Glance.Services;

namespace Glance;

/// A palette entry. Run returns a line to show before the palette closes ("Copied 1.2.3.4"), or null to close at once.
/// OnSearch: only listed once something is typed (notes, clipboard), so the empty list stays short.
public record Cmd(string Title, string Kind, string Icon, Func<Task<string?>> Run, string Extra = "", bool OnSearch = false)
{
    public string Search => $"{Title} {Kind} {Extra}";
}

// Ctrl+K: one search box over everything Glance knows.
public partial class MainWindow
{
    const int MaxResults = 50;
    List<Cmd> commands = [];
    int loadId; // bumps on every open/close, so a slow docker call can't fill a palette that was closed meanwhile

    /// Every word must appear somewhere; titles that start with the query rank first, then titles containing it.
    internal static List<Cmd> Match(IEnumerable<Cmd> all, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return all.Where(c => !c.OnSearch).Take(MaxResults).ToList();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all.Where(c => words.All(w => c.Search.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Take(MaxResults).ToList();
    }

    public void OpenPalette()
    {
        Activate();
        if (!PaletteLayer.IsVisible)
        {
            commands = BuiltIn();
            PaletteLayer.Visibility = Visibility.Visible;
            if (IsWeb(Page.Content)) Page.Visibility = Visibility.Hidden; // WebView2 is a native window; WPF can't draw over it
            Motion.To(PaletteLayer, OpacityProperty, 1, 140, 0);
            Motion.To(PaletteZoom, ScaleTransform.ScaleXProperty, 1, 200, 0.97);
            Motion.To(PaletteZoom, ScaleTransform.ScaleYProperty, 1, 200, 0.97);
            PaletteStatus.Text = "";
            Query.Text = "";
            Filter();
            LoadMore(++loadId);
        }
        Dispatcher.BeginInvoke(() => { Query.Focus(); Query.SelectAll(); }, DispatcherPriority.Input);
    }

    void ClosePalette()
    {
        loadId++;
        PaletteLayer.Visibility = Visibility.Collapsed;
        Page.Visibility = Visibility.Visible;
    }

    void Filter()
    {
        var keep = Results.SelectedItem;
        var list = Match(commands, Query.Text);
        Results.ItemsSource = list;
        Results.SelectedIndex = keep is Cmd k && list.IndexOf(k) is var i and >= 0 ? i : 0;
        if (Results.SelectedItem is { } s) Results.ScrollIntoView(s);
    }

    async void Execute(Cmd c)
    {
        PaletteStatus.Text = "Working…";
        string? message;
        try { message = await c.Run(); }
        catch (Exception e) { message = "Failed: " + e.Message; Db.Warn($"palette {c.Title}: {e.Message}"); }
        if (message is null) { ClosePalette(); return; }
        PaletteStatus.Text = message;
        await Task.Delay(1500);
        if (PaletteLayer.IsVisible && PaletteStatus.Text == message) ClosePalette();
    }

    List<Cmd> BuiltIn()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claude = Shell.OnPath("claude.exe");
        var list = Tabs.Append(SettingsTab).Select(t => new Cmd(t.Name, "Page", t.Icon, Do(() => Go(t)))).ToList();
        // A merged page's other parts (History, Stickies) are still found by name; the first part is the page itself.
        list.AddRange(Tabs.SelectMany(t => t.Parts?.Skip(1) ?? []).Select(p => new Cmd(p.Name, "Page", p.Icon, Do(() => Go(p.Name)))));

        list.Add(new("New terminal", "CLI", "\uE756", Do(() => Cli(home))));
        if (claude) list.Add(new("New Claude chat", "CLI", "\uE99A", Do(() => Cli(home, CliPage.Claude, "Claude"))));
        list.Add(new("Copy public IP", "Network", "\uE774", async () =>
        {
            var ip = await Net.PublicIp();
            return ClipboardHistory.Copy(ip.Ip) ? $"Copied {ip.Ip}" : "Clipboard busy, try again.";
        }));
        list.Add(new("Generate password", "Password", "\uE8D7", () =>
            Task.FromResult<string?>(ClipboardHistory.CopySecret(Passwords.Generate(20, true, true, true, true, true).Password, TimeSpan.FromSeconds(30))
                ? "Copied a 20-character password · clipboard clears in 30s" : "Clipboard busy, try again.")));

        list.Add(new("New sticky", "Stickies", "\uE840", Do(() => { Go("Stickies"); Stickies.Add(NoteKind.Text); })));
        list.Add(new(App.Settings.StickiesShown ? "Hide stickies" : "Show stickies", "Stickies", "\uE840",
            Do(() => Stickies.SetShown(!App.Settings.StickiesShown))));
        list.Add(new("Pick color from screen", "Color", "\uE790", async () =>
        {
            if (await Eyedropper.Pick() is not { } c) return "Cancelled.";
            ((ColorPage)ShowTab("Color")).Use(c, remember: true);
            var hex = ColorMath.Hex(c);
            return ClipboardHistory.Copy(hex) ? $"Copied {hex}" : "Clipboard busy, try again.";
        }));
        list.Add(new(App.Settings.StayOpen ? "Stay open: turn off" : "Stay open: turn on", "Glance", "\uE718",
            Do(() => SetStayOpen(!App.Settings.StayOpen))));
        if (App.DeployScript is not null) list.Add(new("Rebuild and restart Glance", "Glance", "\uE72C", Do(App.Rebuild)));
        else list.Add(new("Check for updates", "Glance", "", Do(App.CheckForUpdates)));

        foreach (var dir in App.Settings.CliFolders)
        {
            var leaf = Path.GetFileName(dir.TrimEnd('\\')) is { Length: > 0 } l ? l : dir;
            list.Add(new($"Terminal in {leaf}", "Folder", "\uED25", Do(() => Cli(dir)), dir));
            if (claude) list.Add(new($"Claude in {leaf}", "Folder", "\uE99A", Do(() => Cli(dir, CliPage.Claude, "Claude")), dir, true));
        }

        foreach (var n in NoteStore.Notes)
            list.Add(new(n.DisplayTitle, "Note", n.Icon, Do(() => { Go("Notes"); NoteStore.RequestFocus(n); }),
                n.Text + " " + string.Join(" ", n.Items.Select(i => i.Text)), true));

        foreach (var p in PinStore.Pins)
            list.Add(new(p.Name, "Pin", "\uE707", Do(() => ((MapPage)ShowTab("Map")).ShowPin(p)), $"{p.Note} {p.Address} {p.Kind}", true));

        foreach (var c in ClipboardHistory.Items.Where(c => !c.IsImage))
        {
            var line = string.Join(" ", c.Text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            list.Add(new(line.Length > 90 ? line[..90] + "…" : line, $"Clipboard · {c.When}", "\uE77F",
                () => Task.FromResult<string?>(ClipboardHistory.Copy(c.Text) ? "Copied" : "Clipboard busy, try again."), c.Text, true));
        }
        return list;
    }

    // The slow sources: a folder scan and `docker ps`. Appended as they arrive.
    async void LoadMore(int id)
    {
        var repos = await Task.Run(Git.Folders);
        if (id != loadId) return;
        var claude = Shell.OnPath("claude.exe");
        foreach (var (dir, name) in repos)
        {
            commands.Add(new(name, "Repo", "\uF003", Do(() => Cli(dir)), dir));
            if (claude) commands.Add(new($"Claude in {name}", "Repo", "\uE99A", Do(() => Cli(dir, CliPage.Claude, "Claude")), dir, true));
        }
        Filter();

        if (!Tabs.Any(t => t.Name == "Docker")) return;
        var (state, containers) = await Docker.Containers();
        if (id != loadId || state != Docker.State.Ok) return;
        foreach (var c in containers)
        {
            var verb = c.Running ? "stop" : "start";
            commands.Add(new($"{(c.Running ? "Stop" : "Start")} {c.Names}", "Docker", c.Running ? "\uE71A" : "\uE768",
                async () => await Docker.Do(verb, c.ID) ?? $"{(c.Running ? "Stopped" : "Started")} {c.Names}", c.Image));
            if (c.Running) commands.Add(new($"Restart {c.Names}", "Docker", "\uE72C",
                async () => await Docker.Do("restart", c.ID) ?? $"Restarted {c.Names}", c.Image, true));
        }
        Filter();
    }

    static void Cli(string dir, string? command = null, string? name = null) => ((App)Application.Current).OpenCli(dir, command, name);

    static Func<Task<string?>> Do(Action a) => () => { a(); return Task.FromResult<string?>(null); };

    void Go(Tab t) => Open(t);

    void Go(string name) => ShowTab(name);

    // Pages hosting a WebView2 (a native child window): no fades or slides, nothing drawn over them.
    static bool IsWeb(object? page) => page is CliPage or MapPage;

    void PaletteClick(object sender, RoutedEventArgs e) => OpenPalette();

    void PaletteBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == PaletteLayer) ClosePalette();
    }

    void QueryChanged(object sender, TextChangedEventArgs e)
    {
        Results.SelectedIndex = -1; // new query: start from the best match
        Filter();
    }

    void QueryKey(object sender, KeyEventArgs e)
    {
        var n = Results.Items.Count;
        if (e.Key is Key.Down or Key.Up && n > 0)
        {
            Results.SelectedIndex = (Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + n) % n;
            Results.ScrollIntoView(Results.SelectedItem);
        }
        else if (e.Key == Key.Enter && Results.SelectedItem is Cmd c) Execute(c);
        else return;
        e.Handled = true;
    }

    void ResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: Cmd c }) Execute(c);
    }
}
