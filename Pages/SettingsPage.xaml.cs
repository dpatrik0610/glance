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

        BlurBox.IsChecked = s.Blur;
        TopBox.IsChecked = s.Topmost;
        FBox.IsChecked = s.Fahrenheit;
        BarBox.IsChecked = s.TopBar;
        AlertsBox.IsChecked = s.AlertsEnabled;
        HotkeyBox.Text = s.Hotkey;
        CityBox.Text = s.City;
        MongoBox.Text = s.MongoUrl;
        IsVisibleChanged += (_, _) =>
        {
            AutoBox.IsChecked = Settings.AutoStart; // re-read: may change outside the app
            MenuBox.IsChecked = Settings.ExplorerMenu;
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
        s.Fahrenheit = FBox.IsChecked == true;
        s.TopBar = BarBox.IsChecked == true;
        s.AlertsEnabled = AlertsBox.IsChecked == true;
        if (sender == AutoBox) Settings.AutoStart = AutoBox.IsChecked == true;
        if (sender == MenuBox) Settings.ExplorerMenu = MenuBox.IsChecked == true;
        App.ApplySettings();
    }

    void SetHotkey(object sender, RoutedEventArgs e)
    {
        s.Hotkey = HotkeyBox.Text.Trim();
        App.ApplySettings();
        HotkeyStatus.Text = App.HotkeyStatus;
    }

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
