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
        new("Exchange", "Life", "\uE8AB", () => new ExchangePage()),
        new("Clipboard", "Tools", "\uE77F", () => new ClipboardPage()),
        new("Notes", "Tools", "\uE70B", () => new NotesPage()),
        new("Password", "Tools", "\uE8D7", () => new PasswordPage()),
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
        Nav.SelectedIndex = 0;
        App.SettingsChanged += ApplySettings;
        PreviewKeyDown += Shortcut;
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
        Native.SetBlur(hwnd, s.Blur);
    }

    // Shared by both sidebar lists; selecting in one clears the other so only one item is ever highlighted.
    void NavChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not Tab t) return;
        (list == Nav ? Bottom : Nav).SelectedItem = null;
        if (!pages.TryGetValue(t, out var page)) pages[t] = page = t.Create();
        Page.Content = page;
        PageTitle.Text = t.Name;
        // The terminal is a native window: it can't fade or move with WPF content, so it just appears.
        if (page is not CliPage) Motion.Rise(Page, PageShift);
    }

    /// Called when the window comes up from hidden: the content settles in.
    public void Reveal() => Motion.Rise(Body, BodyShift, 8, 220);

    public UserControl ShowTab(string name)
    {
        var t = Tabs.First(t => t.Name == name);
        Nav.SelectedItem = t; // creates the page if needed
        return pages[t];
    }

    // Ctrl+K searches, Ctrl+1..9 jumps to a tab, Ctrl+Tab / Ctrl+Shift+Tab cycles, Ctrl+, opens Settings, Esc closes the palette or hides.
    void Shortcut(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape && PaletteLayer.IsVisible) ClosePalette();
        else if (e.Key == Key.Escape) Hide();
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

    void HideClick(object sender, RoutedEventArgs e) => Hide();

    // Closing (Alt+F4) just hides; the tray menu is the only way out.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
