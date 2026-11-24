using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Glance.Services;

public class Settings
{
    public double Opacity { get; set; } = 0.78;
    public double Glow { get; set; } = 0.6;
    /// The color palette (Skin.Names); applied at startup.
    public string Theme { get; set; } = "Mint";
    public bool Blur { get; set; } = true;
    public bool Topmost { get; set; }
    public bool NavCollapsed { get; set; }
    /// Sidebar groups folded to their header.
    public List<string> FoldedGroups { get; set; } = [];
    /// Pages left out of the sidebar (and the compact strip); still in Ctrl+K and the "…" menu.
    public List<string> HiddenTabs { get; set; } = [];
    /// Pages, and parts of merged pages, turned off entirely: not in the sidebar, "…" or Ctrl+K (Settings → Pages).
    public List<string> OffPages { get; set; } = ["Cloudflare"];
    /// Smaller window with the tabs as icons across the top instead of the sidebar.
    public bool Compact { get; set; }
    /// Glance stays up when focus goes to another window (normally a window dropped from the bar slides back up).
    public bool StayOpen { get; set; }
    public int RefreshMs { get; set; } = 1000;
    public string Hotkey { get; set; } = "Win+Shift+G";
    /// Quick capture into today's note ("" = off).
    public string CaptureHotkey { get; set; } = "Win+Shift+N";

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
    public bool ClipboardImages { get; set; } = true;
    /// Your own Google OAuth client ("Desktop app" type) for the Calendar page. Google treats a desktop client's secret as
    /// not secret; the sign-in itself is kept encrypted elsewhere (GoogleCalendar).
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    /// Calendar id → shown in Glance. Calendars not in here follow their tick in Google Calendar's own list.
    public Dictionary<string, bool> CalendarsShown { get; set; } = [];
    /// The stickies are on the desktop (each also has its own switch).
    public bool StickiesShown { get; set; } = true;
    /// The Color page's recent picks, newest first ("#RRGGBB").
    public List<string> RecentColors { get; set; } = [];

    /// Disk page. Clean: item id → ticked, only where it differs from the item's default. Scan: the drive or folder last picked.
    public Dictionary<string, bool> CleanItems { get; set; } = [];
    public string ScanPath { get; set; } = "";

    public bool Fahrenheit { get; set; }
    /// The symbols the Stocks part of the Money page follows.
    public List<string> StockSymbols { get; set; } = [];
    /// Before OffPages (1.1.13 and earlier): read once by Load, then dropped from the file.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? StocksShown { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CloudflareShown { get; set; }
    /// The always-on-top strip at the top of the screen, and which view it was left on.
    public bool TopBar { get; set; } = true;
    public double BarOpacity { get; set; } = 0.94;
    public string BarView { get; set; } = "System";
    public bool BarHideFullscreen { get; set; } = true;
    /// Over a maximized window (browser tabs), the bar shrinks to a thin line until hovered.
    public bool BarSlim { get; set; } = true;
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

    // Every store lives under here. The selftest gets a throwaway folder, so it never reads or saves over the real data
    // (it runs next to the live Glance on every "Rebuild and restart").
    public static readonly string Dir = Environment.GetCommandLineArgs().Contains("--selftest")
        ? Path.Combine(Path.GetTempPath(), $"glance-selftest-data-{Environment.ProcessId}")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
    static readonly string File_ = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
        if (s.StocksShown == false && !s.OffPages.Contains("Stocks")) s.OffPages.Add("Stocks");
        if (s.CloudflareShown == true) s.OffPages.Remove("Cloudflare");
        s.StocksShown = s.CloudflareShown = null;
        return s;
    }

    public void Save() => WriteAtomic(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// A JSON store's contents; default when there's no file yet. A file that won't parse is renamed aside and logged,
    /// so the next save can't write over what might be recovered from it. Other IO errors (locked, no access) throw.
    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return default; }
        catch (JsonException e)
        {
            var parked = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, parked);
            Db.Error($"{Path.GetFileName(path)} unreadable, moved to {parked}", e);
            return default;
        }
    }

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
