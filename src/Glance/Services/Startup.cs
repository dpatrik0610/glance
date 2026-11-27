using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Glance.Services;

public enum StartupKind { Registry, Folder, Task, Service }

/// One thing Windows starts on its own. `Target` is what a switch changes: "HKCU|Run|value" (a StartupApproved value,
/// as Task Manager keeps them), a task path, or a service name; empty when it can't be switched (RunOnce, policies).
public sealed record StartupItem(string Name, string Command, StartupKind Kind, string Where, string Source, string Target, bool Enabled, bool Windows)
{
    public string? File { get; } = Startup.Exe(Command);
    /// The service is running, or a process of this program is (in any session).
    public bool Running { get; init; }
    public bool Missing => File is not null && Path.IsPathRooted(File) && !System.IO.File.Exists(File);
    public bool CanToggle => Target != "";
    /// Where, shortened for the caption: the CurrentVersion part of a key, just the name of a Startup folder file.
    public string WhereText => Kind == StartupKind.Folder ? Path.GetFileName(Where)
        : Where.Replace(@"\Software\WOW6432Node\Microsoft\Windows\CurrentVersion", @"\WOW6432Node\…").Replace(@"\Software\Microsoft\Windows\CurrentVersion", @"\…");
    public string KindText => Kind switch
    {
        StartupKind.Registry => (Source.EndsWith("RunOnce") ? "Run once" : Source.Contains("Policies") ? "Policy" : "Run key")
            + (Where.Contains("WOW6432Node") ? " · 32-bit" : "") + (Where.StartsWith("HKLM") ? " · all users" : ""),
        StartupKind.Folder => "Startup folder" + (Target.StartsWith("HKLM") ? " · all users" : ""),
        StartupKind.Task => "Task · " + Source.ToLowerInvariant(),
        _ => "Service · " + Source.ToLowerInvariant(),
    };
}

/// Everything that starts with Windows or at sign-in: Run and RunOnce keys (64- and 32-bit, policies too), both Startup
/// folders, scheduled tasks with a logon or boot trigger, and automatic services. Like Autoruns, minus the exotic places.
public static class Startup
{
    const string Cv = @"Software\Microsoft\Windows\CurrentVersion";
    const string Approved = Cv + @"\Explorer\StartupApproved";
    static readonly string Windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // Key, hive, the StartupApproved list that switches it (null: nothing does), and a short word for the scope.
    static readonly (RegistryHive Hive, string Key, string? Approved)[] Keys =
    [
        (RegistryHive.CurrentUser, Cv + @"\Run", "Run"),
        (RegistryHive.LocalMachine, Cv + @"\Run", "Run"),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32"),
        (RegistryHive.CurrentUser, Cv + @"\RunOnce", null),
        (RegistryHive.LocalMachine, Cv + @"\RunOnce", null),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", null),
        (RegistryHive.CurrentUser, Cv + @"\Policies\Explorer\Run", null),
        (RegistryHive.LocalMachine, Cv + @"\Policies\Explorer\Run", null),
    ];

    static RegistryKey Root(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
    static string Short(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
    static RegistryHive Hive(string s) => s == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    public static List<StartupItem> List()
    {
        var list = new List<StartupItem>();
        foreach (var (hive, key, approved) in Keys)
        {
            using var k = Root(hive).OpenSubKey(key);
            if (k is null) continue;
            foreach (var name in k.GetValueNames().Where(n => n != ""))
            {
                var command = k.GetValue(name)?.ToString() ?? "";
                var on = approved is null || IsOn(hive, approved, name);
                list.Add(new(name, command, StartupKind.Registry, $"{Short(hive)}\\{key}", key,
                    approved is null ? "" : $"{Short(hive)}|{approved}|{name}", on, UnderWindows(Exe(command))));
            }
        }
        foreach (var (hive, folder) in new[] { (RegistryHive.CurrentUser, Environment.SpecialFolder.Startup), (RegistryHive.LocalMachine, Environment.SpecialFolder.CommonStartup) })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var command = Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcut(f) ?? f : f;
                var file = Path.GetFileName(f);
                list.Add(new(Path.GetFileNameWithoutExtension(f), command, StartupKind.Folder, f, dir, $"{Short(hive)}|StartupFolder|{file}",
                    IsOn(hive, "StartupFolder", file), UnderWindows(Exe(command))));
            }
        }
        try { Tasks(list); }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException) { Db.Warn("startup tasks: " + e.Message); }
        Services(list);
        var services = RunningServices();
        var programs = ProcessPaths();
        return list.Select(i => i with
        {
            Running = i.Kind == StartupKind.Service ? services.Contains(i.Target) : i.File is { } f && programs.ContainsKey(f),
        }).ToList();
    }

    // ---- StartupApproved: 12 bytes, an odd first byte means off (then a FILETIME of when). No value = on. ----

    static bool IsOn(RegistryHive hive, string list, string name)
    {
        using var k = Root(hive).OpenSubKey($@"{Approved}\{list}");
        return Approved_(k?.GetValue(name) as byte[]);
    }

    public static bool Approved_(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    public static byte[] ApprovedValue(bool on)
    {
        var b = new byte[12];
        b[0] = (byte)(on ? 2 : 3);
        if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4);
        return b;
    }

    // ---- Scheduled tasks (Task Scheduler's COM API, hidden ones included) ----

    const int TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0, EnumHidden = 1;

    static dynamic Scheduler()
    {
        dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        s.Connect();
        return s;
    }

    static void Tasks(List<StartupItem> list)
    {
        var stack = new Stack<dynamic>();
        stack.Push(Scheduler().GetFolder("\\"));
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            dynamic tasks;
            try
            {
                foreach (dynamic f in folder.GetFolders(0)) stack.Push(f);
                tasks = folder.GetTasks(EnumHidden);
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException) { continue; } // a folder this user can't open
            foreach (dynamic t in tasks)
                try
                {
                    var def = t.Definition;
                    var when = "";
                    foreach (dynamic tr in def.Triggers)
                        if ((int)tr.Type is TriggerLogon or TriggerBoot) when = (int)tr.Type == TriggerLogon ? "At sign-in" : "At boot";
                    if (when == "") continue;
                    var command = "";
                    foreach (dynamic a in def.Actions)
                        if ((int)a.Type == ActionExec) { string? exe = a.Path, args = a.Arguments; command = Quote(exe ?? "") + (string.IsNullOrEmpty(args) ? "" : " " + args); break; }
                    string path = t.Path;
                    list.Add(new((string)t.Name, command, StartupKind.Task, path, when, path, (bool)t.Enabled,
                        path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || UnderWindows(Exe(command))));
                }
                catch (Exception e) when (e is COMException or UnauthorizedAccessException) { } // a task whose definition this user can't read
        }
    }

    // ---- Services: automatic, trigger-started (Manual ones Windows starts on an event), other apps' Manual ones (their
    //      app starts them) and disabled ones ----

    static void Services(List<StartupItem> list)
    {
        using var root = Root(RegistryHive.LocalMachine).OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("Start") is not int start || k.GetValue("Type") is not int type || (type & 0x30) == 0) continue; // Win32 services only, no drivers
            string mode;
            if (start == 2) mode = k.GetValue("DelayedAutostart") is 1 ? "Automatic (delayed)" : "Automatic";
            else if (start == 4) mode = "Disabled";
            else if (start == 3) mode = k.GetSubKeyNames().Contains("TriggerInfo") ? "On trigger" : "Manual";
            else continue;
            var command = k.GetValue("ImagePath")?.ToString() ?? "";
            var windows = UnderWindows(Exe(command));
            if (mode == "Manual" && windows) continue; // Windows' own on-demand services: hundreds, and none start by themselves
            var display = k.GetValue("DisplayName")?.ToString() is { } d && !d.StartsWith('@') ? d : name;
            list.Add(new(display, command, StartupKind.Service, name, mode, name, start != 4, windows));
        }
    }

    /// The sc.exe start mode a service has, from its Source word, to give back when it's switched on again.
    public static string ModeOf(StartupItem service) => service.Source switch
    {
        "Automatic (delayed)" => "delayed-auto",
        "On trigger" or "Manual" => "demand",
        _ => "auto",
    };

    // ---- Switching ----

    /// Turns an entry on or off. A service turned off is Disabled (nothing can start it, not even a trigger or an app);
    /// back on, it gets `mode` (an sc.exe start= word). Throws UnauthorizedAccessException when it takes an administrator.
    public static async Task Set(StartupKind kind, string target, bool on, string mode = "auto")
    {
        switch (kind)
        {
            case StartupKind.Registry or StartupKind.Folder:
                var p = target.Split('|', 3);
                using (var k = Root(Hive(p[0])).CreateSubKey($@"{Approved}\{p[1]}", true))
                    k.SetValue(p[2], ApprovedValue(on), RegistryValueKind.Binary);
                break;
            case StartupKind.Task:
                await Task.Run(() =>
                {
                    try
                    {
                        dynamic task = Scheduler().GetFolder("\\").GetTask(target);
                        task.Enabled = on;
                    }
                    catch (System.Reflection.TargetInvocationException e) when (e.InnerException is { } inner)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
                    }
                });
                break;
            case StartupKind.Service:
                var r = await Shell.Run("sc.exe", "config", target, "start=", on ? mode : "disabled");
                if (r.Code == 5) throw new UnauthorizedAccessException();
                if (r.Code != 0) throw new InvalidOperationException(r.Out.Trim() is { Length: > 0 } o ? o : $"sc.exe exited with {r.Code}");
                break;
        }
    }

    // ---- Stopping what's running ----

    /// Stops a service (by name) or ends every process of a program (by path). Waits until it's gone.
    static async Task Stop(StartupKind kind, string target)
    {
        if (kind == StartupKind.Service)
        {
            var r = await Shell.Run("sc.exe", "stop", target);
            if (r.Code == 5) throw new UnauthorizedAccessException();
            if (r.Code is not (0 or 1062)) throw new InvalidOperationException(r.Out.Trim() is { Length: > 0 } o ? o : $"sc.exe exited with {r.Code}"); // 1062: wasn't running
            for (var end = DateTime.Now.AddSeconds(20); DateTime.Now < end; await Task.Delay(300))
                if (!RunningServices(target).Contains(target)) return;
            throw new InvalidOperationException("It was asked to stop but is still running after 20 seconds.");
        }
        var denied = false;
        foreach (var id in ProcessPaths().GetValueOrDefault(target) ?? [])
            try { using var p = Process.GetProcessById(id); p.Kill(); await p.WaitForExitAsync(); }
            catch (Win32Exception) { denied = true; } // elevated, or another user's
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { } // already gone
        if (denied) throw new UnauthorizedAccessException();
    }

    /// Running services' names; just `only`'s when given.
    static HashSet<string> RunningServices(string? only = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var where = only is null ? "" : $" AND Name = '{only.Replace("\\", "\\\\").Replace("'", "\\'")}'";
        try
        {
            using var search = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE State = 'Running'{where}");
            foreach (var o in search.Get()) using (o) set.Add((string)o["Name"]);
        }
        catch (ManagementException e) { Db.Warn("startup services: " + e.Message); }
        return set;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(int access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    /// Every running program's path with its process ids. Limited query rights: elevated processes have a path too.
    static Dictionary<string, List<int>> ProcessPaths()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var name = new StringBuilder(1024);
        foreach (var p in Process.GetProcesses())
            using (p)
            using (var h = OpenProcess(0x1000, false, p.Id)) // PROCESS_QUERY_LIMITED_INFORMATION
            {
                var size = name.Capacity;
                if (h.IsInvalid || !QueryFullProcessImageName(h, 0, name, ref size)) continue;
                var path = name.ToString(0, size);
                if (!map.TryGetValue(path, out var ids)) map[path] = ids = [];
                ids.Add(p.Id);
            }
        return map;
    }

    // ---- Doing it, as an administrator when it takes one ----

    /// on, off or stop. Stop's target is the service name or the program's path.
    static Task Do(string verb, StartupKind kind, string target, string mode) =>
        verb == "stop" ? Stop(kind, target) : Set(kind, target, verb == "on", mode);

    /// The elevated half: `--startup <on|off|stop> <kind> <base64 target> <mode> <error file>`. Exit code 0 = done;
    /// otherwise the reason is in the error file. The work runs on the thread pool: awaited on this (UI) thread, it would deadlock.
    public static int Run(string[] args)
    {
        try
        {
            var target = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
            Task.Run(() => Do(args[0], Enum.Parse<StartupKind>(args[1]), target, args[3])).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            try { System.IO.File.WriteAllText(args[4], e.Message); } catch (Exception) { }
            return 1;
        }
    }

    /// Do, through an elevated copy of Glance (one UAC prompt) when this one isn't allowed. Returns what went wrong, or null.
    static async Task<string?> Perform(string verb, StartupKind kind, string target, string mode = "auto")
    {
        try { await Do(verb, kind, target, mode); return null; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException && !Native.IsAdmin)
        {
            var errors = Path.GetTempFileName();
            try
            {
                var arg = Convert.ToBase64String(Encoding.UTF8.GetBytes(target));
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--startup {verb} {kind} {arg} {mode} \"{errors}\"")
                    { UseShellExecute = true, Verb = "runas" })!;
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await p.WaitForExitAsync(wait.Token);
                return p.ExitCode == 0 ? null : System.IO.File.ReadAllText(errors) is { Length: > 0 } why ? why : "The administrator copy couldn't do it.";
            }
            catch (Win32Exception) { return "Needs an administrator: the prompt was declined."; }
            catch (OperationCanceledException) { return "The administrator copy didn't finish within a minute."; }
            finally { try { System.IO.File.Delete(errors); } catch (IOException) { } }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return "Access denied."; }
        catch (Exception e) when (e is IOException or COMException or InvalidOperationException or Win32Exception) { return e.Message; }
    }

    /// Turns an entry on or off, then reads it back: the change must be there. Returns what went wrong, or null.
    public static async Task<string?> Switch(StartupItem item, bool on, string mode = "auto")
    {
        if (await Perform(on ? "on" : "off", item.Kind, item.Target, mode) is { } error) return error;
        try
        {
            if (await Task.Run(() => IsOn(item)) != on)
                return $"Windows still reports it {(on ? "off" : "on")}. Something switched it back, or it's protected (some Windows services and tasks are).";
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException) { Db.Warn("startup check: " + e.Message); }
        return null;
    }

    /// Stops the service, or ends the program's processes. Returns what went wrong, or null.
    public static Task<string?> StopNow(StartupItem item) =>
        item.Kind == StartupKind.Service ? Perform("stop", item.Kind, item.Target)
        : item.File is { } f ? Perform("stop", item.Kind, f) : Task.FromResult<string?>("No program path to stop.");

    /// The entry's state as Windows has it now.
    public static bool IsOn(StartupItem item)
    {
        switch (item.Kind)
        {
            case StartupKind.Task:
                dynamic task = Scheduler().GetFolder("\\").GetTask(item.Target);
                return (bool)task.Enabled;
            case StartupKind.Service:
                using (var k = Root(RegistryHive.LocalMachine).OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{item.Target}"))
                    return k?.GetValue("Start") is int start && start != 4;
            default:
                var p = item.Target.Split('|', 3);
                return IsOn(Hive(p[0]), p[1], p[2]);
        }
    }

    /// Opens where the entry is registered: Registry Editor at the key, Explorer at the file, Task Scheduler or Services.
    public static void ShowSource(StartupItem item)
    {
        switch (item.Kind)
        {
            case StartupKind.Registry:
                var full = item.Where.Replace("HKCU\\", "HKEY_CURRENT_USER\\").Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\");
                using (var k = Registry.CurrentUser.CreateSubKey(Cv + @"\Applets\Regedit"))
                    k.SetValue("LastKey", @"Computer\" + full);
                Shell.Open("regedit.exe"); // opens at LastKey; asks for an administrator itself
                break;
            case StartupKind.Folder: Shell.Reveal(item.Where); break;
            case StartupKind.Task: Shell.Open("taskschd.msc"); break;
            case StartupKind.Service: Shell.Open("services.msc"); break;
        }
    }

    // ---- Paths ----

    static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    /// Windows' own: a Microsoft program under the Windows folder. Vendors put theirs there too (DriverStore, System32), so the folder alone isn't enough.
    static bool UnderWindows(string? file)
    {
        if (file is null || !file.StartsWith(Windir + "\\", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(file)) return false;
        try { return FileVersionInfo.GetVersionInfo(file).CompanyName?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true; }
        catch (IOException) { return false; }
    }

    /// The program a command line starts: quoted, or the shortest unquoted prefix that is a file, or a bare name in System32.
    public static string? Exe(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c == "") return null;
        if (c.StartsWith('"')) return c.IndexOf('"', 1) is > 1 and var end ? c[1..end] : null;
        if (c.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) c = Windir + c[11..];
        else if (c.StartsWith(@"\??\")) c = c[4..];
        else if (c.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) c = Path.Combine(Windir, c);
        for (var i = c.IndexOf(' '); ; i = c.IndexOf(' ', i + 1))
        {
            var head = i < 0 ? c : c[..i];
            if (i < 0 && !Path.IsPathRooted(head)) break; // a bare name: never against the current folder (System32 at sign-in)
            if (Path.IsPathRooted(head) && System.IO.File.Exists(head)) return head;
            if (Path.IsPathRooted(head) && System.IO.File.Exists(head + ".exe")) return head + ".exe";
            if (i < 0) break;
        }
        var first = c.Split(' ')[0];
        if (Path.IsPathRooted(first)) return first;
        var sys = Path.Combine(Environment.SystemDirectory, first);
        return System.IO.File.Exists(sys) ? sys : System.IO.File.Exists(sys + ".exe") ? sys + ".exe" : null;
    }

    static string? Shortcut(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var s = shell.CreateShortcut(lnk);
            string target = s.TargetPath, args = s.Arguments;
            return target == "" ? null : Quote(target) + (args == "" ? "" : " " + args);
        }
        catch (COMException) { return null; }
    }
}
