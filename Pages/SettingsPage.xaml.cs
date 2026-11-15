using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Glance.Services;

namespace Glance.Pages;

public partial class SettingsPage : UserControl
{
    readonly Settings s = App.Settings;

    public SettingsPage()
    {
        InitializeComponent();
        // Released builds check here; a build from source updates by "Rebuild and restart".
        var source = App.DeployScript is not null;
        VersionText.Text = $"Version {Updater.Current}" + (source ? ", built from source" : "");
        UpdateButton.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        Bind(OpacityRow, s.Opacity, v => s.Opacity = Math.Round(v, 2), v => $"{v:P0}");
        Bind(GlowRow, s.Glow, v => s.Glow = Math.Round(v, 2), v => $"{v:P0}");
        Bind(BarOpacityRow, s.BarOpacity, v => s.BarOpacity = Math.Round(v, 2), v => $"{v:P0}");
        Bind(RefreshRow, s.RefreshMs, v => s.RefreshMs = (int)v, v => $"{v / 1000:0.##}s");
        Bind(CpuLimitRow, s.CpuTempLimit, v => s.CpuTempLimit = (float)v, v => $"{v:0}°C");
        Bind(GpuLimitRow, s.GpuTempLimit, v => s.GpuTempLimit = (float)v, v => $"{v:0}°C");
        Bind(RamLimitRow, s.RamLimit, v => s.RamLimit = (float)v, v => $"{v:0}%");
        Bind(DiskLimitRow, s.DiskFreeLimit, v => s.DiskFreeLimit = (float)v, v => $"{v:0}%");
        Bind(SoundVolumeRow, s.AlertSoundVolume, v => s.AlertSoundVolume = Math.Round(v, 2), v => $"{v:P0}");
        foreach (var name in Chime.Names)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "sound", Content = name, Margin = new(0, 0, 6, 6), IsChecked = name == s.AlertSoundName };
            chip.Checked += (_, _) =>
            {
                if (!IsLoaded) return; // the initial check
                s.AlertSoundName = name;
                App.ApplySettings();
                Chime.Play(preview: true);
            };
            Sounds.Children.Add(chip);
        }

        BlurBox.IsChecked = s.Blur;
        TopBox.IsChecked = s.Topmost;
        CompactBox.IsChecked = s.Compact;
        FBox.IsChecked = s.Fahrenheit;
        StocksBox.IsChecked = s.StocksShown;
        BarBox.IsChecked = s.TopBar;
        BarFullscreenBox.IsChecked = s.BarHideFullscreen;
        BarSlimBox.IsChecked = s.BarSlim;
        foreach (RadioButton r in BarSizes.Children) r.IsChecked = (string)r.Content == s.BarSize;
        TrackAlertsBox.IsChecked = s.TrackAlerts;
        AlertsBox.IsChecked = s.AlertsEnabled;
        SoundBox.IsChecked = s.AlertSound;
        HotkeyBox.Text = s.Hotkey;
        CityBox.Text = s.City;
        MongoBox.Text = s.MongoUrl;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Mark.Play();
            AutoBox.IsChecked = Settings.AutoStart; // re-read: may change outside the app
            MenuBox.IsChecked = Settings.ExplorerMenu;
            ClipBox.IsChecked = s.ClipboardHistory; // the Clipboard page has the same switches
            ClipImagesBox.IsChecked = s.ClipboardImages;
            ClipImagesRow.IsEnabled = s.ClipboardHistory;
            ShowHotkeyStatus();
        };
    }

    /// Row layout: [slider][value]. Wires the slider to a setting with live apply.
    static void Bind(Panel row, double value, Action<double> set, Func<double, string> show)
    {
        var slider = (Slider)row.Children[0];
        var text = (TextBlock)row.Children[1];
        slider.Value = value;
        text.Text = show(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            text.Text = show(e.NewValue);
            set(e.NewValue);
            App.ApplySettings();
        };
    }

    void Toggled(object sender, RoutedEventArgs e)
    {
        s.Blur = BlurBox.IsChecked == true;
        s.Topmost = TopBox.IsChecked == true;
        s.Compact = CompactBox.IsChecked == true;
        s.Fahrenheit = FBox.IsChecked == true;
        s.StocksShown = StocksBox.IsChecked == true;
        s.TopBar = BarBox.IsChecked == true;
        s.BarHideFullscreen = BarFullscreenBox.IsChecked == true;
        s.BarSlim = BarSlimBox.IsChecked == true;
        s.TrackAlerts = TrackAlertsBox.IsChecked == true;
        s.AlertsEnabled = AlertsBox.IsChecked == true;
        s.AlertSound = SoundBox.IsChecked == true;
        s.ClipboardHistory = ClipBox.IsChecked == true;
        s.ClipboardImages = ClipImagesBox.IsChecked == true;
        ClipImagesRow.IsEnabled = s.ClipboardHistory;
        if (sender == AutoBox) Settings.AutoStart = AutoBox.IsChecked == true;
        if (sender == MenuBox) Settings.ExplorerMenu = MenuBox.IsChecked == true;
        App.ApplySettings();
    }

    void BarSizeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // the initial check above
        s.BarSize = (string)((RadioButton)sender).Content;
        App.ApplySettings();
    }

    void SetHotkey(object sender, RoutedEventArgs e)
    {
        s.Hotkey = HotkeyBox.Text.Trim();
        App.ApplySettings();
        ShowHotkeyStatus();
    }

    void ShowHotkeyStatus() => HotkeyRow.Description = $"Shows or hides Glance from anywhere. {App.HotkeyStatus}".TrimEnd();

    void HotkeyKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SetHotkey(sender, e);
    }

    void PreviewSound(object sender, MouseButtonEventArgs e) => Chime.Play(preview: true);

    async void TestAlert(object sender, RoutedEventArgs e) =>
        App.ShowAlert(new Alert("CPU · Temperature", s.CpuTempLimit + 3, s.CpuTempLimit, "°C", true, false, Blame.Cpu), await HardwareMonitor.Top(false));

    void CityKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SetCity(sender, e);
    }

    async void SetCity(object sender, RoutedEventArgs e)
    {
        var q = CityBox.Text.Trim();
        if (q.Length == 0) return;
        CityRow.Description = "Searching…";
        try
        {
            if (await Weather.Find(q) is not { } p)
            {
                CityRow.Description = "Not found.";
                return;
            }
            (s.City, s.Lat, s.Lon) = (p.Name, p.Latitude, p.Longitude);
            CityBox.Text = p.Name;
            CityRow.Description = $"{p.Name}{(p.Country is null ? "" : ", " + p.Country)} · {p.Latitude:0.00}, {p.Longitude:0.00}";
            App.ApplySettings();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CityRow.Description = "Lookup failed, check connection.";
            Db.Error("geocode", ex);
        }
    }

    void SetMongo(object sender, RoutedEventArgs e)
    {
        s.MongoUrl = MongoBox.Text.Trim();
        Db.Init(s.MongoUrl);
        App.ApplySettings();
    }

    void CheckUpdates(object sender, RoutedEventArgs e) => App.CheckForUpdates();

    void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
}

/// One settings line: icon, title and a short description on the left, its control on the right.
/// Rows after the first in a group draw a hairline above; the control is named after the title for screen readers.
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        BorderThickness = Parent is Panel p && p.Children.IndexOf(this) > 0 ? new Thickness(0, 1, 0, 0) : new Thickness(0);
    }

    protected override void OnInitialized(EventArgs e) // after XAML filled the content panel
    {
        base.OnInitialized(e);
        IEnumerable<object> parts = Content is Panel p ? p.Children.Cast<object>() : [Content];
        foreach (var c in parts.OfType<Control>().Where(c => c is not Button && AutomationProperties.GetName(c) == ""))
            AutomationProperties.SetName(c, Title);
    }
}
