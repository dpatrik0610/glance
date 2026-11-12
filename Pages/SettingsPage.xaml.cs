using System.Net.Http;
using System.Windows;
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
        Bind(OpacityRow, s.Opacity, v => s.Opacity = Math.Round(v, 2), v => $"{v:P0}");
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
            AutoBox.IsChecked = Settings.AutoStart; // re-read: may change outside the app
            MenuBox.IsChecked = Settings.ExplorerMenu;
            ClipBox.IsChecked = s.ClipboardHistory; // the Clipboard page has the same switches
            ClipImagesBox.IsChecked = s.ClipboardImages;
            ClipImagesBox.IsEnabled = s.ClipboardHistory;
            HotkeyStatus.Text = App.HotkeyStatus;
        };
    }

    /// Row layout: [label][slider][value]. Wires the slider to a setting with live apply.
    static void Bind(DockPanel row, double value, Action<double> set, Func<double, string> show)
    {
        var text = (TextBlock)row.Children[0];
        var slider = (Slider)row.Children[2];
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
        s.TopBar = BarBox.IsChecked == true;
        s.BarHideFullscreen = BarFullscreenBox.IsChecked == true;
        s.BarSlim = BarSlimBox.IsChecked == true;
        s.TrackAlerts = TrackAlertsBox.IsChecked == true;
        s.AlertsEnabled = AlertsBox.IsChecked == true;
        s.AlertSound = SoundBox.IsChecked == true;
        s.ClipboardHistory = ClipBox.IsChecked == true;
        s.ClipboardImages = ClipImagesBox.IsChecked == true;
        ClipImagesBox.IsEnabled = s.ClipboardHistory;
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
        HotkeyStatus.Text = App.HotkeyStatus;
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
        CityStatus.Text = "Searching…";
        try
        {
            if (await Weather.Find(q) is not { } p)
            {
                CityStatus.Text = "Not found.";
                return;
            }
            (s.City, s.Lat, s.Lon) = (p.Name, p.Latitude, p.Longitude);
            CityBox.Text = p.Name;
            CityStatus.Text = $"{p.Name}{(p.Country is null ? "" : ", " + p.Country)} · {p.Latitude:0.00}, {p.Longitude:0.00}";
            App.ApplySettings();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CityStatus.Text = "Lookup failed, check connection.";
            Db.Error("geocode", ex);
        }
    }

    void SetMongo(object sender, RoutedEventArgs e)
    {
        s.MongoUrl = MongoBox.Text.Trim();
        Db.Init(s.MongoUrl);
        App.ApplySettings();
    }
}
