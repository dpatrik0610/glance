using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Glance.Pages;
using Glance.Services;

namespace Glance;

public partial class MainWindow : Window
{
    /// Needs: an exe that must be on PATH, or the tab is left out (checked at startup).
    public record Tab(string Name, string Group, string Icon, Func<UserControl> Create, string? Needs = null);

    // Add a page here; sidebar, Ctrl+N shortcuts and lazy creation follow. Keep tabs of a group together:
    // groups appear in first-seen order. Icons: Segoe Fluent Icons code points.
    static readonly Tab[] Tabs = new Tab[]
    {
        new("System", "Monitor", "\uE7F4", () => new SystemPage()),
        new("Network", "Monitor", "\uE839", () => new NetworkPage()),
        new("History", "Monitor", "\uE81C", () => new HistoryPage()),
        new("Weather", "Life", "\uE706", () => new WeatherPage()),
        new("Map", "Life", "\uE707", () => new MapPage()),
        new("Exchange", "Life", "\uE8AB", () => new ExchangePage()),
        new("Now Playing", "Life", "\uEC4F", () => new NowPlayingPage()),
        new("Clipboard", "Tools", "\uE77F", () => new ClipboardPage()),
        new("Notes", "Tools", "\uE70B", () => new NotesPage()),
        new("Password", "Tools", "\uE8D7", () => new PasswordPage()),
        new("Color", "Tools", "\uE790", () => new ColorPage()),
        new("CLI", "Dev", "\uE756", () => new CliPage()),
        new("Ports", "Dev", "\uE943", () => new DevPage()),
        new("Git", "Dev", "\uF003", () => new GitPage(), "git.exe"),
        new("Docker", "Dev", "\uF158", () => new DockerPage(), "docker.exe"),
    }.Where(t => t.Needs is null || Shell.OnPath(t.Needs)).ToArray();

    // Pinned at the bottom of the sidebar, icon only.
    static readonly Tab SettingsTab = new("Settings", "", "\uE713", () => new SettingsPage());

    // Pages are built on first visit and kept, so switching back is instant and state survives.
    readonly Dictionary<Tab, UserControl> pages = [];
    IntPtr hwnd;

    public MainWindow()
    {
        InitializeComponent();
        var view = new ListCollectionView(Tabs);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Tab.Group)));
        Nav.ItemsSource = view;
        Bottom.ItemsSource = new[] { SettingsTab };
        Strip.ItemsSource = Tabs.Append(SettingsTab).ToArray();
        Nav.SelectedIndex = 0;
        App.SettingsChanged += ApplySettings;
        PreviewKeyDown += Shortcut;
        Deactivated += (_, _) => Dispatcher.BeginInvoke(LostToOutside, System.Windows.Threading.DispatcherPriority.Input);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Undrop(); };
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
        Panel.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(s.Opacity, 0.1, 1) * 255), 0x16, 0x16, 0x18));
        Topmost = s.Topmost;
        Nav.Tag = !s.NavCollapsed; // item labels and group headers bind to this
        SetCompact(s.Compact);
        PinButton.Content = s.StayOpen ? "\uE842" : "\uE718"; // pinned (filled) : pin
        PinButton.Foreground = s.StayOpen ? (Brush)FindResource("Accent") : (Brush)FindResource("Text");
        PinButton.ToolTip = s.StayOpen ? "Stays open when you click elsewhere" : "Hides when you click elsewhere (click to keep open)";
        Native.SetBlur(hwnd, s.Blur);
    }

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
        Side.Visibility = Brand.Visibility = NavToggle.Visibility = shown;
        Strip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Page.Margin = on ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        Body.Margin = new Thickness(on ? 10 : 14);
        CompactButton.Content = on ? "\uE740" : "\uE73F";
        CompactButton.ToolTip = on ? "Full layout" : "Compact mode";
        if (Dropped) Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2; // stay centered under the bar
    }

    void PinClick(object sender, RoutedEventArgs e) => SetStayOpen(!App.Settings.StayOpen);

    public static void SetStayOpen(bool on)
    {
        App.Settings.StayOpen = on;
        App.ApplySettings();
    }

    void CompactClick(object sender, RoutedEventArgs e)
    {
        App.Settings.Compact = !compact;
        App.ApplySettings();
    }

    // The strip only mirrors the sidebar lists, which stay the source of truth.
    void StripChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Strip.SelectedItem is not Tab t) return;
        if (t == SettingsTab) Bottom.SelectedItem = t; else Nav.SelectedItem = t;
    }

    // Shared by both sidebar lists; selecting in one clears the other so only one item is ever highlighted.
    void NavChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not Tab t) return;
        (list == Nav ? Bottom : Nav).SelectedItem = null;
        Strip.SelectedItem = t;
        if (!pages.TryGetValue(t, out var page)) pages[t] = page = t.Create();
        Page.Content = page;
        PageTitle.Text = t.Name;
        // The terminal is a native window: it can't fade or move with WPF content, so it just appears.
        if (!IsWeb(page)) Motion.Rise(Page, PageShift);
    }

    /// Called when the window comes up from hidden: the content settles in.
    public void Reveal() => Motion.Rise(Body, BodyShift, 8, 220);

    public UserControl ShowTab(string name)
    {
        var t = Tabs.First(t => t.Name == name);
        Nav.SelectedItem = t; // creates the page if needed
        return pages[t];
    }

    // Ctrl+K searches, Ctrl+1..9 jumps to a tab, Ctrl+Tab / Ctrl+Shift+Tab cycles, Ctrl+, opens Settings, Esc closes the palette or hides (not from a terminal).
    void Shortcut(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape && PaletteLayer.IsVisible) ClosePalette();
        // In a terminal Esc belongs to the shell: PSReadLine clears the typed line, Claude stops its reply.
        else if (e.Key == Key.Escape && !(Page.Content is CliPage && e.OriginalSource is Microsoft.Web.WebView2.Wpf.WebView2)) Away();
        else if (ctrl && e.Key == Key.K) OpenPalette();
        else if (ctrl && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < Tabs.Length) Nav.SelectedIndex = e.Key - Key.D1;
        else if (ctrl && e.Key == Key.OemComma) Bottom.SelectedIndex = 0;
        else if (ctrl && e.Key == Key.Tab)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            var from = Nav.SelectedIndex < 0 ? (step > 0 ? -1 : 0) : Nav.SelectedIndex; // from Settings: wrap to first/last
            Nav.SelectedIndex = (from + step + Tabs.Length) % Tabs.Length;
        }
        else return;
        e.Handled = true;
    }

    void ToggleNav(object sender, RoutedEventArgs e)
    {
        App.Settings.NavCollapsed = !App.Settings.NavCollapsed;
        App.ApplySettings();
    }

    void Drag(object sender, MouseButtonEventArgs e) => DragMove();

    void HideClick(object sender, RoutedEventArgs e) => Away();

    // Closing (Alt+F4) just hides; the tray menu is the only way out.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
