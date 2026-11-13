using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace Glance.Services;

/// Safe-to-delete junk: caches and leftovers that Windows and apps rebuild by themselves. One table (Items), one function
/// that both measures and deletes (Sweep), so the estimate is what a clean removes.
public static class Cleaner
{
    /// A folder whose contents go (the folder stays). With Pattern: only the folder's own files that match, no subfolders.
    public record Target(string Root, string? Pattern = null);

    /// On: ticked unless the user says otherwise. Admin: needs the elevated run. MinAge: younger files stay (may be in use).
    /// Busy: process names; while one runs the item is left alone (a browser rewrites these files as it goes).
    public record Item(string Id, string Group, string Name, string Note, bool On, bool Admin, Target[] Targets,
        TimeSpan MinAge = default, string[]? Busy = null)
    {
        public bool Running => Busy?.Any(n => Process.GetProcessesByName(n).Length > 0) == true;
    }

    /// Unreadable: a folder this user can't list, so the size is unknown until the elevated run.
    public record Found(long Bytes, int Skipped = 0, bool Unreadable = false);

    public const string Bin = "bin";

    static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    static readonly string Local = Folder(Environment.SpecialFolder.LocalApplicationData), Roaming = Folder(Environment.SpecialFolder.ApplicationData),
        Home = Folder(Environment.SpecialFolder.UserProfile), Win = Folder(Environment.SpecialFolder.Windows),
        Data = Folder(Environment.SpecialFolder.CommonApplicationData);

    static readonly TimeSpan Day = TimeSpan.FromHours(24);

    // A missing base folder gives a relative path here, which Safe refuses: the item then does nothing.
    static Target T(string start, string rest, string? pattern = null) => new(Path.Combine(start, rest), pattern);

    static IEnumerable<Item> Chromium(string id, string name, string process, string data, string? profiles = null)
    {
        // `*`: each profile folder ("Default", "Profile 1"…). Opera keeps the profile's own files elsewhere (profiles).
        yield return new($"{id}-cache", "Browsers", $"{name} cache", "Pages and scripts kept for faster loading; they download again", true, false,
            [T(Local, $@"{data}\*\Cache"), T(Local, $@"{data}\*\Code Cache"), T(Local, $@"{data}\*\GPUCache"), T(Local, $@"{data}\Cache"),
             T(Local, $@"{data}\ShaderCache"), T(Local, $@"{data}\GrShaderCache"), T(Local, $@"{data}\GraphiteDawnCache")], Busy: [process]);
        var p = profiles ?? Path.Combine(Local, data);
        yield return new($"{id}-history", "Browsers", $"{name} history", "The list of visited pages. Bookmarks, passwords and logins stay", false, false,
            [T(p, "*", "History*"), T(p, "*", "Visited Links"), T(p, "", "History*"), T(p, "", "Visited Links")], Busy: [process]);
    }

    public static readonly Item[] Items =
    [
        new("temp", "Windows", "Temporary files", "Your Temp folder; files from the last 24 hours stay", true, false, [new(Path.GetTempPath().TrimEnd('\\'))], Day),
        new(Bin, "Windows", "Recycle Bin", "Everything in the bin, on every drive", true, false, []),
        new("thumbs", "Windows", "Thumbnail and icon cache", "Explorer draws them again as you browse", true, false,
            [T(Local, @"Microsoft\Windows\Explorer", "thumbcache_*.db"), T(Local, @"Microsoft\Windows\Explorer", "iconcache_*.db")]),
        new("dumps", "Windows", "Crash dumps and error reports", "What apps left behind when they crashed", true, false,
            [T(Local, "CrashDumps"), T(Local, @"Microsoft\Windows\WER\ReportArchive"), T(Local, @"Microsoft\Windows\WER\ReportQueue")]),
        new("shaders", "Windows", "Shader caches", "DirectX, NVIDIA and AMD; games compile them again on first start", true, false,
            [T(Local, "D3DSCache"), T(Local, @"NVIDIA\DXCache"), T(Local, @"NVIDIA\GLCache"), T(Local, @"AMD\DxCache"), T(Local, @"AMD\DxcCache"),
             T(Home, @"AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache")]),

        .. Chromium("chrome", "Chrome", "chrome", @"Google\Chrome\User Data"),
        .. Chromium("edge", "Edge", "msedge", @"Microsoft\Edge\User Data"),
        .. Chromium("brave", "Brave", "brave", @"BraveSoftware\Brave-Browser\User Data"),
        .. Chromium("vivaldi", "Vivaldi", "vivaldi", @"Vivaldi\User Data"),
        .. Chromium("opera", "Opera", "opera", @"Opera Software\Opera Stable", Path.Combine(Roaming, @"Opera Software\Opera Stable")),
        // No Firefox history: it shares places.sqlite with the bookmarks.
        new("firefox-cache", "Browsers", "Firefox cache", "Pages and scripts kept for faster loading; they download again", true, false,
            [T(Local, @"Mozilla\Firefox\Profiles\*\cache2"), T(Local, @"Mozilla\Firefox\Profiles\*\startupCache")], Busy: ["firefox"]),

        new("npm", "Developer", "npm cache", "Downloaded packages; they download again on install", false, false, [T(Local, @"npm-cache\_cacache")]),
        new("bun", "Developer", "Bun cache", "Downloaded packages; they download again on install", false, false, [T(Home, @".bun\install\cache")]),
        new("nuget", "Developer", "NuGet packages", "Every restored package; the next build restores what it needs", false, false,
            [T(Home, @".nuget\packages"), T(Local, @"NuGet\v3-cache"), T(Local, @"NuGet\plugins-cache")]),
        new("pip", "Developer", "pip cache", "Downloaded wheels; they download again on install", false, false, [T(Local, @"pip\Cache")]),

        new("wintemp", "System", "Windows Temp", "The system's Temp folder; files from the last 24 hours stay", true, true, [T(Win, "Temp")], Day),
        new("wu", "System", "Windows Update downloads", "Installers of updates; Windows fetches one again if it still needs it", true, true,
            [T(Win, @"SoftwareDistribution\Download")]),
        new("do", "System", "Delivery Optimization cache", "Update pieces kept to share with other PCs", true, true,
            [T(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")]),
        new("sysdumps", "System", "System error reports and memory dumps", "Left by crashes of Windows itself", true, true,
            [T(Data, @"Microsoft\Windows\WER\ReportArchive"), T(Data, @"Microsoft\Windows\WER\ReportQueue"), T(Win, "Minidump"), T(Win, "", "MEMORY.DMP")]),
    ];

    /// The folders a target stands for: each `*` is every folder at that level. Only ones that exist and pass Safe.
    public static IEnumerable<string> Roots(Target t)
    {
        IEnumerable<string> found = [""];
        foreach (var part in t.Root.TrimEnd('\\').Split('\\'))
            found = part == "*"
                ? found.SelectMany(d => { try { return Directory.GetDirectories(d); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; } })
                : found.Select(d => d.Length == 0 ? part + "\\" : Path.Combine(d, part));
        return found.Select(d => d.TrimEnd('\\')).Where(d => Safe(d, t.Pattern is not null) && Directory.Exists(d));
    }

    /// May this folder's contents be deleted? Never a relative path (a base folder that didn't resolve), a drive root, or a
    /// folder directly under one; never the profile or app-data folders themselves. A pattern target only takes the files
    /// it names from the folder, so it may sit one level higher (C:\Windows for MEMORY.DMP).
    public static bool Safe(string dir, bool pattern = false)
    {
        if (!Path.IsPathFullyQualified(dir) || dir.Contains('*') || dir.Contains("..")) return false;
        var depth = dir.TrimEnd('\\').Count(c => c == '\\');
        if (depth < (pattern ? 1 : 2)) return false;
        return pattern || !new[] { Local, Roaming, Home, Win, Data, Path.Combine(Home, "AppData") }
            .Any(p => p.Length > 0 && string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    // Reparse points (junctions, symlinks) are skipped whole: never entered, never deleted, so nothing outside a root is reached.
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// What a clean would free.
    public static Found Estimate(Item item) => Go(item, false);

    /// Deletes the item's files; Bytes is what went. A file in use or protected is skipped and counted, never forced.
    public static Found Clean(Item item) => item.Running ? new(0) : Go(item, true);

    static Found Go(Item item, bool delete)
    {
        if (item.Id == Bin) return RecycleBin(delete);
        long bytes = 0;
        int skipped = 0;
        var unreadable = false;
        var cutoff = DateTime.UtcNow - item.MinAge;
        foreach (var t in item.Targets)
            foreach (var root in Roots(t))
            {
                // Listing C:\Windows\Temp is denied to a normal user; Sweep would quietly see it as empty.
                try { Directory.EnumerateFileSystemEntries(root).Any(); }
                catch (UnauthorizedAccessException) { unreadable = true; continue; }
                catch (IOException) { continue; }
                Sweep(root, t.Pattern, cutoff, delete, true, ref bytes, ref skipped);
            }
        return new(bytes, skipped, unreadable);
    }

    /// Measures (delete = false) or deletes the files under dir that were last written before cutoff. Subfolders in parallel;
    /// one left empty is removed, the root itself never.
    public static void Sweep(string dir, string? pattern, DateTime cutoff, bool delete, bool top, ref long bytes, ref int skipped)
    {
        long b = 0;
        int s = 0;
        List<string>? subs = null;
        try
        {
            foreach (var (path, isDir, length, written) in new FileSystemEnumerable<(string, bool, long, DateTime)>(dir,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcDateTime), Flat))
            {
                if (isDir) { if (pattern is null) (subs ??= []).Add(path); continue; }
                if (pattern is not null && !FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path))) continue;
                if (written > cutoff) continue;
                if (delete)
                    try { File.Delete(path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s++; continue; }
                b += length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (subs is not null)
            Parallel.ForEach(subs, () => (Bytes: 0L, Skipped: 0),
                (sub, _, acc) => { Sweep(sub, null, cutoff, delete, false, ref acc.Bytes, ref acc.Skipped); return acc; },
                acc => { Interlocked.Add(ref b, acc.Bytes); Interlocked.Add(ref s, acc.Skipped); });
        if (delete && !top)
            try { Directory.Delete(dir); } // not recursive: only goes when nothing was kept in it
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        bytes += b;
        skipped += s;
    }

    struct BinInfo { public int Size; public long Bytes, Count; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string? root, ref BinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);

    static Found RecycleBin(bool empty)
    {
        var info = new BinInfo { Size = Marshal.SizeOf<BinInfo>() };
        if (SHQueryRecycleBinW(null, ref info) != 0 || info.Count == 0) return new(0);
        const uint NoConfirm = 1, NoProgress = 2, NoSound = 4;
        return !empty || SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirm | NoProgress | NoSound) == 0 ? new(info.Bytes) : new(0, (int)info.Count);
    }

    /// `Glance.exe --clean id,id`, elevated: cleans those items and returns the megabytes freed as the exit code, the one
    /// number the page wants back. Ids only, looked up in Items: this process never takes a path from its command line.
    public static int Run(string[] ids)
    {
        long bytes = 0;
        Parallel.ForEach(Items.Where(i => ids.Contains(i.Id)), i => Interlocked.Add(ref bytes, Clean(i).Bytes));
        return (int)Math.Min(bytes >> 20, int.MaxValue);
    }
}
