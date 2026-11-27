using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glance.Services;

public enum StartupKind { Registry, Folder, Task, Service }

/// One thing Windows starts on its own. `Target` is what a switch changes: "HKCU|Run|value" (a StartupApproved value,
/// as Task Manager keeps them), a task path, or a service name; empty when it can't be switched (RunOnce, policies).
public sealed record StartupItem(string Name, string Command, StartupKind Kind, string Where, string Source, string Target, bool Enabled, bool Windows)
{
    public string? File { get; } = Startup.Exe(Command);
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

    public static List<StartupItem> List(ICollection<string> servicesOff)
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
        Services(list, servicesOff);
        return list;
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

    // ---- Services: automatic ones, plus those this page set to Manual ----

    static void Services(List<StartupItem> list, ICollection<string> off)
    {
        using var root = Root(RegistryHive.LocalMachine).OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("Start") is not int start || k.GetValue("Type") is not int type || (type & 0x30) == 0) continue; // Win32 services only, no drivers
            if (start != 2 && !(start == 3 && off.Contains(name))) continue;
            var command = k.GetValue("ImagePath")?.ToString() ?? "";
            var display = k.GetValue("DisplayName")?.ToString() is { } d && !d.StartsWith('@') ? d : name;
            var delayed = k.GetValue("DelayedAutostart") is 1;
            list.Add(new(display, command, StartupKind.Service, name, delayed ? "Automatic (delayed)" : "Automatic", name, start == 2, UnderWindows(Exe(command))));
        }
    }

    // ---- Switching ----

    /// Turns an entry on or off. Throws UnauthorizedAccessException when it takes an administrator (the caller elevates).
    public static async Task Set(StartupKind kind, string target, bool on)
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
                // ponytail: back on is plain Automatic; a service that was delayed comes back undelayed.
                var r = await Shell.Run("sc.exe", "config", target, "start=", on ? "auto" : "demand");
                if (r.Code == 5) throw new UnauthorizedAccessException();
                if (r.Code != 0) throw new InvalidOperationException(r.Out.Trim() is { Length: > 0 } o ? o : $"sc.exe exited with {r.Code}");
                break;
        }
    }

    /// The elevated half: `--startup <kind> <base64 target> 1|0`. Exit code 0 = done.
    public static int Run(string[] args)
    {
        try
        {
            var target = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            Set(Enum.Parse<StartupKind>(args[0]), target, args[2] == "1").GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e) { Db.Warn("startup --set: " + e.Message); return 1; }
    }

    /// Set, through an elevated copy of Glance (one UAC prompt) when this one isn't allowed. Returns an error, or null.
    public static async Task<string?> Switch(StartupItem item, bool on)
    {
        try { await Set(item.Kind, item.Target, on); return null; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException && !Native.IsAdmin)
        {
            try
            {
                var arg = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(item.Target));
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--startup {item.Kind} {arg} {(on ? 1 : 0)}")
                    { UseShellExecute = true, Verb = "runas" })!;
                await p.WaitForExitAsync();
                return p.ExitCode == 0 ? null : "The administrator copy couldn't change it. Details are in the log.";
            }
            catch (Win32Exception) { return "Needs an administrator: the prompt was declined."; }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return "Access denied."; }
        catch (Exception e) when (e is IOException or COMException or InvalidOperationException or Win32Exception) { return e.Message; }
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
            if (System.IO.File.Exists(head)) return head;
            if (System.IO.File.Exists(head + ".exe")) return head + ".exe";
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
