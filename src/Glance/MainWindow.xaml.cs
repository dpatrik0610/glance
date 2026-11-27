using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Glance.Pages;
using Glance.Services;

namespace Glance;

/// A page's bar under the title row: State on the left (one line of counts), Header (its controls) on the right.
/// Tabs (a merged page's chips) sit in the title row, right after the page name.
/// A page that OwnsTitleRow (the CLI) gets no title or bar; its Header goes in the window's title row instead.
public interface IHeader
{
    FrameworkElement? Header { get; }
    FrameworkElement? State => null;
    FrameworkElement? Tabs => null;
    bool OwnsTitleRow => false;
}

public partial class MainWindow : Window
{
    /// Takes a page's bar out of its spot in the page's XAML so the title row can show it (IHeader).
    public static FrameworkElement Lift(FrameworkElement bar)
    {
        (bar.Parent as Panel)?.Children.Remove(bar);
        bar.Margin = default;
        bar.VerticalAlignment = VerticalAlignment.Center;
        return bar;
    }

    /// Needs: an exe that must be on PATH, or the tab is left out (checked at startup).
    /// Parts: a merged page, its related pages switched by chips at the top (Tabbed).
    /// Keep: never dropped when idle; it holds work or input that isn't saved anywhere (shells, jobs, scans, typed text).
    /// Any page, and any part after a merged page's first, can be turned off in Settings → Pages (Features).
    public record Tab(string Name, string Group, string Icon, Func<UserControl> Create, string? Needs = null, Tabbed.Part[]? Parts = null, bool Keep = false);

    static Tab Merged(string name, string group, string icon, params Tabbed.Part[] parts) =>
        new(name, group, icon, () => new Tabbed(parts), Parts: parts);

    static Tab Kept(string name, string group, string icon, params Tabbed.Part[] parts) =>
        new(name, group, icon, () => new Tabbed(parts), Parts: parts, Keep: true);

    // Add a page here; sidebar, Ctrl+N shortcuts and lazy creation follow. Keep tabs of a group together:
    // groups appear in first-seen order. Icons: Segoe Fluent Icons code points.
    internal static readonly Tab[] Tabs = new Tab[]
    {
        new("Today", "Home", "", () => new TodayPage()),
        Merged("System", "Monitor", "\uE7F4", new("Live", "\uE7F4", () => new SystemPage()), new("History", "\uE81C", () => new HistoryPage()),
            new("Analysis", "\uE9D9", () => new AnalysisPage()), new("Startup", "\uE7B5", () => new StartupPage())),
        new("Network", "Monitor", "\uE839", () => new NetworkPage()),
        Kept("Disk", "Monitor", "\uEDA2", new("Clean", "\uEA99", () => new CleanPage()), new("Scan", "\uEB05", () => new ScanPage())),
        new("Weather", "Life", "\uE706", () => new WeatherPage()),
        new("Map", "Life", "\uE707", () => new MapPage()),
        Merged("Money", "Life", "\uE825", new("Budget", "\uE8C7", () => new BudgetPage()), new("Bills", "\uE787", () => new BillsPage()),
            new("Exchange", "\uE8AB", () => new ExchangePage()), new("Stocks", "\uE9D2", () => new StocksPage())),
        new("Now Playing", "Life", "\uEC4F", () => new NowPlayingPage()),
        new("Clipboard", "Tools", "\uE77F", () => new ClipboardPage()),
        new("Notes", "Tools", "\uE70B", () => new NotesPage()),
        new("Board", "Tools", "\uE80A", () => new BoardPage(), Keep: true),
        new("Tasks", "Tools", "\uE73A", () => new TasksPage()),
        new("Timer", "Tools", "\uE916", () => new TimerPage()),
        new("Calendar", "Tools", "\uE787", () => new CalendarPage()),
        new("Password", "Tools", "\uE8D7", () => new PasswordPage()),
        new("Color", "Tools", "\uE790", () => new ColorPage()),
        Kept("Converters", "Tools", "\uE895", new("Images", "\uEB9F", () => new ImagePage()), new("Media", "\uE714", () => new MediaPage())),
        new("Compare", "Tools", "\uE8C8", () => new ComparePage(), Keep: true),
        new("CLI", "Dev", "\uE756", () => new CliPage(), Keep: true),
        new("Ports", "Dev", "\uE943", () => new DevPage()),
        new("Git", "Dev", "\uF003", () => new GitPage(), "git.exe"),
        new("Docker", "Dev", "\uF158", () => new DockerPage(), "docker.exe"),
        new("Cloudflare", "Dev", "\uE753", () => new CloudflarePage()),
        new("JSON", "Dev", "\uE8A5", () => new JsonPage(), Keep: true),
    }.Where(t => t.Needs is null || Shell.OnPath(t.Needs)).ToArray();

    // Pinned at the bottom of the sidebar, icon only.
    static readonly Tab SettingsTab = new("Settings", "", "\uE713", () => new SettingsPage());

    // Pages are built on first visit and kept, so switching back is instant and state survives.
    // One left alone for IdleDrop is dropped (see Sweep) and built again on the next visit.
    readonly Dictionary<Tab, UserControl> pages = [];
    readonly Dictionary<Tab, long> leftAt = []; // Environment.TickCount64 when the page was last on screen
    static readonly TimeSpan IdleDrop = TimeSpan.FromMinutes(10);
    IntPtr hwnd;
    readonly ListCollectionView navView, stripView;

    sealed class Conv(Func<object, object?> f) : IValueConverter
    {
        public object? Convert(object value, Type t, object p, CultureInfo c) => f(value);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// Group name → folded. Read when the group's container is built, so changes go through RefreshNav.
    public static readonly IValueConverter Folded = new Conv(v => v is string g && App.Settings.FoldedGroups.Contains(g));

    public static readonly IValueConverter Upper = new Conv(v => (v as string)?.ToUpperInvariant());

    /// A sidebar item's live value, one per page name, made on first use; most stay empty.
    public sealed class Badge : INotifyPropertyChanged
    {
        string text = "";
        Brush brush = Skin.Brush("Sub");
        public string Text { get => text; set { if (text != value) { text = value; Changed(); } } }
        public Brush Brush { get => brush; set { if (brush != value) { brush = value; Changed(); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
    }

    static readonly Dictionary<string, Badge> badges = [];
    static Badge BadgeFor(string name) => badges.TryGetValue(name, out var b) ? b : badges[name] = new();
    public static readonly IValueConverter BadgeOf = new Conv(v => BadgeFor((string)v));

    static bool Enabled(Tab t) => Features.On(t.Name);
    static bool Shown(object o) => Enabled((Tab)o) && !App.Settings.HiddenTabs.Contains(((Tab)o).Name);
    /// Hidden from the sidebar by the user: still in the "…" menu.
    static bool Tucked(Tab t) => Enabled(t) && !Shown(t);
    string enabledTabs = ""; // which tabs are on, to rebuild the views when a setting flips one

    public MainWindow()
    {
        InitializeComponent();
        // The default size, shrunk to fit small screens (CenterScreen then centers what is left)
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        navView = new ListCollectionView(Tabs) { Filter = Shown };
        navView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Tab.Group)));
        Nav.ItemsSource = navView;
        Bottom.ItemsSource = new[] { SettingsTab };
        stripView = new ListCollectionView(Tabs.Append(SettingsTab).ToList()) { Filter = o => (Tab)o == SettingsTab || Shown(o) };
        Strip.ItemsSource = stripView;
        MoreButton.Visibility = Tabs.Any(Tucked) ? Visibility.Visible : Visibility.Collapsed;
        enabledTabs = EnabledTabs();
        if (Nav.Items.Count > 0) Nav.SelectedIndex = 0; else Open(Tabs.FirstOrDefault(Enabled) ?? SettingsTab);
        App.SettingsChanged += ApplySettings;
        PreviewKeyDown += Shortcut;
        Deactivated += (_, _) => Dispatcher.BeginInvoke(LostToOutside, System.Windows.Threading.DispatcherPriority.Input);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Undrop(); else trimmed = false; };
        new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMinutes(2), System.Windows.Threading.DispatcherPriority.ApplicationIdle, (_, _) => Sweep(), Dispatcher);
        VersionText.Text = "v" + Updater.Current;
        BadgeFor("Timer").Brush = Skin.Brush("Accent");
        App.Sampled += s => Dispatcher.BeginInvoke(() => Sampled(s));
        new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background, (_, _) => TimerBadge(), Dispatcher);
    }

    // ---- Live values: sidebar badges (only drawn while the window is up) ----

    void Sampled(Snapshot s)
    {
        if (!IsVisible) return;
        BadgeFor("System").Text = s.CpuLoad is { } c ? $"{c:0}%" : "";
        BadgeFor("Network").Text = s.NetDown >= 100_000 ? Units.Rate(s.NetDown) : ""; // quiet when idle
    }

    /// The Timer item shows what's counting: the Pomodoro phase or the soonest timer, else a running stopwatch.
    void TimerBadge()
    {
        if (!IsVisible) return;
        var d = Timers.Data;
        var now = DateTime.Now;
        var ends = d.Timers.Where(t => t.Ends is not null).Select(t => t.Ends!.Value).Append(d.PhaseEnds ?? DateTime.MaxValue).Min();
        BadgeFor("Timer").Text = ends != DateTime.MaxValue ? Timers.Clock((ends - now).TotalSeconds)
            : d.WatchStarted is { } w ? Timers.Clock(d.WatchSeconds + (now - w).TotalSeconds) : "";
    }

    // Drops pages nobody has looked at for a while: their controls, bitmaps and handlers go, and a compacting GC
    // hands the memory back. The page on screen and Keep pages stay. Pages that listen to app-wide events
    // unsubscribe in Dispose, or the event would keep the dropped page (and its work) alive.
    bool trimmed; // collected since the window was last shown

    void Sweep()
    {
        var now = Environment.TickCount64;
        var dropped = false;
        foreach (var (t, page) in pages.ToArray())
        {
            if (t.Keep || page == Page.Content || page == SettingsHost.Content || now - leftAt.GetValueOrDefault(t, now) < IdleDrop.TotalMilliseconds) continue;
            pages.Remove(t);
            leftAt.Remove(t);
            (page as IDisposable)?.Dispose();
            dropped = true;
        }
        // Also once per hide: a tray app sitting for hours should give back what the last session used.
        if (!dropped && (IsVisible || trimmed)) return;
        trimmed = !IsVisible;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // ---- Dropped from the top bar: hangs under it, slides back up on Esc, the arrow, or a click elsewhere ----

    public bool Dropped { get; private set; }
    public event Action<bool>? DroppedChanged;
    Rect home; // where the window was before, restored when it goes back up
    bool homeMaximized;

    /// Places the (hidden or shown) window centered under the bar and slides it down into place.
    public void DropUnder(double top)
    {
        if (!Dropped)
        {
            home = new Rect(Left, Top, Width, Height);
            homeMaximized = WindowState == WindowState.Maximized;
            Dropped = true;
            DroppedChanged?.Invoke(true);
        }
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Height = Math.Min(Height, area.Bottom - top - 8);
        BeginAnimation(TopProperty, null);
        Top = top + 6;
        Motion.To(this, TopProperty, top + 6, 260, from: top - 18, done: () => BeginAnimation(TopProperty, null));
    }

    /// Hides: dropped, it slides up into the bar first.
    public void Away()
    {
        if (!Dropped || !IsVisible) { Hide(); return; }
        Motion.To(Body, OpacityProperty, 0, 150);
        Motion.To(this, TopProperty, Top - 18, 180, done: () => { BeginAnimation(TopProperty, null); Hide(); });
    }

    // Focus went elsewhere (not to one of our own dialogs): a dropped Glance goes back up.
    void LostToOutside()
    {
        if (!App.Settings.StayOpen && Dropped && IsVisible && !IsActive && !OwnedWindows.Cast<Window>().Any(w => w.IsActive)) Away();
    }

    void Undrop()
    {
        if (!Dropped) return;
        Dropped = false;
        BeginAnimation(TopProperty, null);
        (Left, Top, Width, Height) = (home.Left, home.Top, home.Width, home.Height);
        if (homeMaximized) WindowState = WindowState.Maximized;
        DroppedChanged?.Invoke(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        Native.RoundedDarkFrame(hwnd);
        HwndSource.FromHwnd(hwnd)!.AddHook(Native.KeepFrameActive);
        ApplySettings();
    }

    void ApplySettings()
    {
        var s = App.Settings;
        Panel.Background = new SolidColorBrush(Skin.Get("PanelColor", (byte)(Math.Clamp(s.Opacity, 0.1, 1) * 255)));
        Glow.Opacity = s.Glow;
        Topmost = s.Topmost;
        ShowNav(!s.NavCollapsed);
        SetCompact(s.Compact);
        PinButton.Content = s.StayOpen ? "\uE842" : "\uE718"; // pinned (filled) : pin
        PinButton.Foreground = s.StayOpen ? Skin.Brush("Accent") : Skin.Brush("Text");
        PinButton.ToolTip = s.StayOpen ? "Stays open when you click elsewhere" : "Hides when you click elsewhere (click to keep open)";
        Native.SetBlur(hwnd, s.Blur);
        if (EnabledTabs() is var on && on != enabledTabs) { enabledTabs = on; RefreshViews(); }
    }

    static string EnabledTabs() => string.Join(",", Tabs.Where(Enabled).Select(t => t.Name));

    // ---- Compact mode: smaller window, no sidebar, the tabs as icons across the top. Same pages, other frame. ----

    bool compact;
    Size other = new(880, 600); // the size of the mode we're not in, swapped on toggle

    void SetCompact(bool on)
    {
        if (on == compact) return;
        compact = on;
        var keep = new Size(Width, Height);
        (Width, Height) = (other.Width, other.Height);
        other = keep;
        var shown = on ? Visibility.Collapsed : Visibility.Visible;
        NavToggle.Visibility = shown;
        PageTitle.Visibility = on || (Page.Content as IHeader)?.OwnsTitleRow == true ? Visibility.Collapsed : Visibility.Visible;
        Side.Visibility = on || !navShown ? Visibility.Collapsed : Visibility.Visible;
        Strip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Body.Margin = new Thickness(on ? 10 : 14);
        SearchHint.Visibility = shown; // compact: just the icon and the key next to the strip
        SearchField.Width = on ? double.NaN : 300;
        if (Dropped) Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2; // stay centered under the bar
    }

    void PinClick(object sender, RoutedEventArgs e) => SetStayOpen(!App.Settings.StayOpen);

    public static void SetStayOpen(bool on)
    {
        App.Settings.StayOpen = on;
        App.ApplySettings();
    }

    // The strip only mirrors the sidebar lists, which stay the source of truth.
    void StripChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Strip.SelectedItem is Tab t) Open(t);
    }

    // Shared by both sidebar lists; selecting in one clears the other so only one item is ever highlighted.
    void NavChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not Tab t) return;
        if (t == SettingsTab) { OpenSettings(); return; }
        Show(t);
    }

    /// Every way to a page goes through here: a sidebar item when it has one, else the page alone (hidden from the sidebar).
    void Open(Tab t)
    {
        if (t == SettingsTab) OpenSettings();
        else if (Nav.Items.Contains(t)) Nav.SelectedItem = t;
        else { Nav.SelectedItem = Bottom.SelectedItem = null; Show(t); }
    }

    /// Puts the open page's tabs, controls and state in the title row and the bar below it.
    /// A merged page calls it again when it switches parts, as each part brings its own bar.
    internal void ApplyHead(UserControl page)
    {
        if (Page.Content != page) return;
        var head = page as IHeader;
        var own = head?.OwnsTitleRow == true;
        TitleSlot.Content = own ? head!.Header : head?.Tabs;
        HeaderSlot.Content = own ? null : head?.Header;
        StateSlot.Content = head?.State;
        PageHead.Visibility = own || head?.Header is null && head?.State is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Show(Tab t)
    {
        Strip.SelectedItem = Strip.Items.Contains(t) ? t : null;
        // Reaching a page in a folded group (Ctrl+K, Ctrl+N) opens the group, so the highlight is visible.
        if (Nav.Items.Contains(t) && App.Settings.FoldedGroups.Remove(t.Group)) Dispatcher.BeginInvoke(RefreshNav);
        if (!pages.TryGetValue(t, out var page)) pages[t] = page = t.Create();
        if (Page.Content == page) return; // re-selected after a sidebar refresh
        CloseSettings(); // Ctrl+1..9 or the palette went to another page
        if (pages.FirstOrDefault(p => p.Value == Page.Content).Key is { } left) leftAt[left] = Environment.TickCount64;
        Page.Content = page;
        PageTitle.Text = t.Name;
        ApplyHead(page);
        var own = (page as IHeader)?.OwnsTitleRow == true;
        SearchButton.Visibility = own ? Visibility.Collapsed : Visibility.Visible; // the CLI: tabs only, Ctrl+K still works
        PageTitle.Visibility = own || compact ? Visibility.Collapsed : Visibility.Visible;
        Page.Margin = own ? new Thickness(12, 0, 12, 12) : new Thickness(24, 4, 24, 18);
        // The terminal is a native window: it can't fade or move with WPF content, so it just appears.
        if (!IsWeb(page)) Motion.Rise(Page, PageShift);
    }

    /// Called when the window comes up from hidden: the content settles in.
    public void Reveal()
    {
        Motion.Rise(Body, BodyShift, 8, 220);
        Mark.Play();
    }

    /// Opens a page, or a part of a merged page (History, Exchange), and returns it.
    public UserControl ShowTab(string name)
    {
        var t = Tabs.FirstOrDefault(t => t.Parts?.Any(p => p.Name == name) == true) ?? Tabs.First(t => t.Name == name);
        Open(t); // creates the page if needed
        return pages[t] is not Tabbed tabbed ? pages[t] : tabbed.Has(name) ? tabbed.Show(name) : tabbed.Current;
    }

    // ---- Settings: a modal over the current page; its section headings become the list on its left ----

    List<TextBlock> sections = [];

    void OpenSettings()
    {
        // The gear and the compact strip don't stay selected: the page underneath is still the current one
        Bottom.SelectedItem = null;
        var current = pages.FirstOrDefault(p => p.Value == Page.Content).Key;
        Strip.SelectedItem = current is not null && Strip.Items.Contains(current) ? current : null;
        if (SettingsLayer.IsVisible) return;
        if (!pages.TryGetValue(SettingsTab, out var page)) pages[SettingsTab] = page = SettingsTab.Create();
        SettingsHost.Content = page;
        SettingsLayer.Visibility = Visibility.Visible;
        if (IsWeb(Page.Content)) Page.Visibility = Visibility.Hidden; // WebView2 is a native window; WPF can't draw over it
        Motion.To(SettingsLayer, OpacityProperty, 1, 140, 0);
        Motion.To(SettingsZoom, ScaleTransform.ScaleXProperty, 1, 200, 0.97);
        Motion.To(SettingsZoom, ScaleTransform.ScaleYProperty, 1, 200, 0.97);
        // After layout: only sections shown (some follow Settings → Pages), top to bottom, left column first on a row
        Dispatcher.BeginInvoke(() =>
        {
            var style = page.TryFindResource("Section");
            sections = Logical(page).OfType<TextBlock>().Where(t => style is not null && t.Style == style && t.IsVisible)
                .OrderBy(t => Math.Round(t.TranslatePoint(default, page).Y)).ThenBy(t => t.TranslatePoint(default, page).X).ToList();
            SettingsNav.ItemsSource = sections.Select(t => t.Text).ToList();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void CloseSettings()
    {
        if (!SettingsLayer.IsVisible) return;
        SettingsLayer.Visibility = Visibility.Collapsed;
        SettingsHost.Content = null; // lets Sweep drop it later
        leftAt[SettingsTab] = Environment.TickCount64;
        if (!PaletteLayer.IsVisible) Page.Visibility = Visibility.Visible;
    }

    void CloseSettingsClick(object sender, RoutedEventArgs e) => CloseSettings();

    void SettingsBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SettingsLayer) CloseSettings();
    }

    // Scrolls the section's heading to the top of the settings.
    void SettingsNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav.SelectedIndex is not (>= 0 and var i) || i >= sections.Count) return;
        DependencyObject? d = sections[i];
        while (d is not (null or ScrollViewer)) d = VisualTreeHelper.GetParent(d);
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset(sv.VerticalOffset + sections[i].TranslatePoint(default, sv).Y - 4);
    }

    static IEnumerable<DependencyObject> Logical(DependencyObject d) =>
        LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().SelectMany(Logical).Prepend(d);

    // ---- Folding groups and hiding pages: both saved in settings, then the views are rebuilt ----

    void RefreshNav()
    {
        RefreshViews();
        App.ApplySettings(); // saves
    }

    void RefreshViews()
    {
        var current = pages.FirstOrDefault(p => p.Value == Page.Content).Key;
        navView.Refresh();
        stripView.Refresh();
        MoreButton.Visibility = Tabs.Any(Tucked) ? Visibility.Visible : Visibility.Collapsed;
        if (current is not null && current != SettingsTab)
        {
            Nav.SelectedItem = Nav.Items.Contains(current) ? current : null;
            Strip.SelectedItem = Strip.Items.Contains(current) ? current : null;
        }
    }

    void GroupClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CollectionViewGroup { Name: string g }) return;
        if (!App.Settings.FoldedGroups.Remove(g)) App.Settings.FoldedGroups.Add(g);
        RefreshNav();
    }

    Tab? menuTab; // the sidebar item the right-click menu is for

    void NavMenuOpening(object sender, ContextMenuEventArgs e)
    {
        menuTab = (ItemsControl.ContainerFromElement(Nav, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as Tab;
        if (menuTab is null) { e.Handled = true; return; } // a group header or empty space
        HideItem.Header = $"Hide {menuTab.Name} from sidebar";
    }

    void HideTab(object sender, RoutedEventArgs e)
    {
        if (menuTab is null) return;
        App.Settings.HiddenTabs.Add(menuTab.Name);
        RefreshNav();
    }

    // The hidden pages: click one to open it, the eye to put it back in the sidebar.
    void MoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var t in Tabs.Where(Tucked))
        {
            var eye = new TextBlock
            {
                Text = "", FontFamily = Skin.Icons, Foreground = Skin.Brush("Sub"),
                Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show in sidebar", Background = Brushes.Transparent,
            };
            var row = new DockPanel();
            DockPanel.SetDock(eye, Dock.Right);
            row.Children.Add(eye);
            row.Children.Add(new TextBlock { Text = t.Icon, FontFamily = Skin.Icons, Width = 26, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = t.Name });
            var item = new MenuItem { Header = row };
            item.Click += (_, _) => Open(t);
            eye.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true; // not a click on the item: the menu stays open for more
                App.Settings.HiddenTabs.Remove(t.Name);
                menu.Items.Remove(item);
                if (menu.Items.Count == 0) menu.IsOpen = false;
                RefreshNav();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // Ctrl+K searches, Ctrl+1..9 jumps to a tab, Ctrl+Tab / Ctrl+Shift+Tab cycles, Esc closes the palette or hides (not from a terminal).
    void Shortcut(object sender, KeyEventArgs e)
    {
        // AltGr arrives as Ctrl+Alt: on e.g. Hungarian keyboards AltGr+, is ";" and AltGr+K is "ł", which are typing, not shortcuts.
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (e.Key == Key.Escape && PaletteLayer.IsVisible) ClosePalette();
        else if (e.Key == Key.Escape && SettingsLayer.IsVisible) CloseSettings();
        // In a terminal Esc belongs to the shell: PSReadLine clears the typed line, Claude stops its reply.
        else if (e.Key == Key.Escape && !(Page.Content is CliPage && e.OriginalSource is Microsoft.Web.WebView2.Wpf.WebView2)) Away();
        else if (ctrl && e.Key == Key.K) OpenPalette();
        // Numbers and cycling follow the sidebar as shown (hidden pages left out).
        else if (ctrl && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < Nav.Items.Count) Nav.SelectedIndex = e.Key - Key.D1;
        else if (ctrl && e.Key == Key.Tab && Nav.Items.Count > 0)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            var from = Nav.SelectedIndex < 0 ? (step > 0 ? -1 : 0) : Nav.SelectedIndex; // from Settings or a hidden page: wrap to first/last
            Nav.SelectedIndex = (from + step + Nav.Items.Count) % Nav.Items.Count;
        }
        else return;
        e.Handled = true;
    }

    // ---- Sidebar: the hamburger slides it fully out and back (Side clips, its width animates; the pane keeps its width meanwhile) ----

    bool navShown = true;

    void ShowNav(bool show)
    {
        if (show == navShown) return;
        navShown = show;
        NavToggle.ToolTip = show ? "Hide sidebar" : "Show sidebar";
        if (compact) return; // no sidebar there; SetCompact restores it
        if (!IsLoaded) { Side.Visibility = show ? Visibility.Visible : Visibility.Collapsed; return; }

        var from = Side.IsVisible ? Side.ActualWidth : 0;
        Side.Visibility = Visibility.Visible;
        Pane.Width = double.NaN;
        Pane.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = Pane.DesiredSize.Width; // includes the gap to the page
        Pane.Width = full - Pane.Margin.Left - Pane.Margin.Right;
        Motion.To(Pane, OpacityProperty, show ? 1 : 0, 220);
        Motion.To(Side, WidthProperty, show ? full : 0, 260, from, () =>
        {
            if (navShown != show) return; // toggled again mid-slide
            if (!show) { Side.Visibility = Visibility.Collapsed; return; }
            Side.BeginAnimation(WidthProperty, null); // back to sizing to its content
            Side.Width = Pane.Width = double.NaN;
        });
    }

    void ToggleNav(object sender, RoutedEventArgs e)
    {
        App.Settings.NavCollapsed = !App.Settings.NavCollapsed;
        App.ApplySettings();
    }

    // Double-click the bar to maximize/restore, like a normal title bar.
    void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaximizeClick(sender, e);
        else DragMove();
    }

    void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
        // A maximized frameless window overhangs the screen by its resize border; pull the content back in.
        Panel.Margin = max ? SystemParameters.WindowResizeBorderThickness : default;
    }

    void HideClick(object sender, RoutedEventArgs e) => Away();

    // Closing (Alt+F4) just hides; the tray menu is the only way out.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
