using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Glance.Services;

public class Settings
{
    public double Opacity { get; set; } = 0.78;
    public bool Blur { get; set; } = true;
    public bool Topmost { get; set; }
    public bool NavCollapsed { get; set; }
    /// Smaller window with the tabs as icons across the top instead of the sidebar.
    public bool Compact { get; set; }
    public int RefreshMs { get; set; } = 1000;
    public string Hotkey { get; set; } = "Win+Shift+G";

    public bool AlertsEnabled { get; set; } = true;
    /// The two-note chime with alerts and reminders.
    public bool AlertSound { get; set; } = true;
    public string AlertSoundName { get; set; } = "Chime";
    public double AlertSoundVolume { get; set; } = 0.6;
    public float CpuTempLimit { get; set; } = 85;
    public float GpuTempLimit { get; set; } = 85;
    public float RamLimit { get; set; } = 90;
    public float DiskFreeLimit { get; set; } = 10;

    public bool ClipboardHistory { get; set; } = true;

    public bool Fahrenheit { get; set; }
    /// The always-on-top strip at the top of the screen, and which view it was left on.
    public bool TopBar { get; set; } = true;
    public double BarOpacity { get; set; } = 0.94;
    public string BarView { get; set; } = "System";
    public bool BarHideFullscreen { get; set; } = true;
    /// Small, Medium or Large: the bar and the alerts under it.
    public string BarSize { get; set; } = "Small";
    /// A small alert when the song changes (Now Playing).
    public bool TrackAlerts { get; set; } = true;
    public string City { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    /// The Map page is locked to this city or country (empty: the Weather hometown, once looked up).
    public string MapArea { get; set; } = "";
    /// South, west, north, east of MapArea.
    public double[]? MapBounds { get; set; }
    public string MongoUrl { get; set; } = "mongodb://localhost:27017";
    public List<string> CliFolders { get; set; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        @"F:\Coding",
    ];

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
    static readonly string File_ = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save() => WriteAtomic(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// Write-then-rename, so a crash mid-write never leaves a truncated file.
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Lives in the registry, not the JSON, so it can't drift from reality.
    public static bool AutoStart
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Glance") is not null;
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Glance", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("Glance", false);
        }
    }

    // Explorer right-click "Open CLI": empty space inside a folder, on a folder, on a drive.
    // ponytail: classic menu only (Win11 shows it under "Show more options"); the new menu needs a signed sparse MSIX package.
    static readonly string[] MenuKeys =
        [@"Software\Classes\Directory\Background\shell\GlanceCli", @"Software\Classes\Directory\shell\GlanceCli", @"Software\Classes\Drive\shell\GlanceCli"];

    public static bool ExplorerMenu
    {
        get => Registry.CurrentUser.OpenSubKey(MenuKeys[0]) is not null;
        set
        {
            foreach (var key in MenuKeys)
            {
                if (!value) { Registry.CurrentUser.DeleteSubKeyTree(key, false); continue; }
                using var k = Registry.CurrentUser.CreateSubKey(key);
                k.SetValue("", "Open CLI");
                k.SetValue("Icon", $"\"{Environment.ProcessPath}\",0");
                using var c = k.CreateSubKey("command");
                c.SetValue("", $"\"{Environment.ProcessPath}\" --cli \"%V\"");
            }
        }
    }
}
