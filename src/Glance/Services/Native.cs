using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Glance.Services;

public record PortInfo(int Port, string Address, int Pid, string Process);
public record ConnInfo(string Process, int Pid, string Remote, int Port);

public static class Native
{
    [DllImport("user32.dll")] static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);

    /// Clicks never take focus from the app you're in (WS_EX_NOACTIVATE), and it stays out of Alt+Tab (WS_EX_TOOLWINDOW).
    public static void Passive(IntPtr hwnd)
    {
        const int ExStyle = -20, NoActivate = 0x08000000, ToolWindow = 0x80;
        SetWindowLongPtr(hwnd, ExStyle, GetWindowLongPtr(hwnd, ExStyle) | NoActivate | ToolWindow);
    }

    /// Out of Alt+Tab (WS_EX_TOOLWINDOW), but it can still take focus to type in.
    public static void ToolWindow(IntPtr hwnd) => SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 0x80);

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    /// A game, fullscreen video or presentation is in front (Windows' own "don't disturb" signal, the one toasts respect).
    public static bool FullscreenApp() =>
        SHQueryUserNotificationState(out var s) == 0 && s is 2 /* busy */ or 3 /* D3D fullscreen */ or 4 /* presentation */;

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    /// Changes whenever anything is put on the clipboard.
    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")] public static extern uint ClipboardSequence();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    struct Rect { public int Left, Top, Right, Bottom; }

    /// The window in front is maximized under `over` (another app's, not Glance's): its title bar or tabs sit behind it.
    public static bool MaximizedUnder(IntPtr over)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsZoomed(fg)) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return false;
        if (!GetWindowRect(fg, out var a) || !GetWindowRect(over, out var b)) return false;
        return a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom; // same monitor
    }

    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---- Native Windows 11 frame: acrylic blur, anti-aliased rounded corners, shadow, dark border ----

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    /// Windows swaps acrylic for a solid color on inactive windows. Answering WM_NCACTIVATE as "still active"
    /// keeps the glass; WPF's own activation (IsActive, focus) follows WM_ACTIVATE and is unaffected.
    public static IntPtr KeepFrameActive(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != 0x86 /* WM_NCACTIVATE */ || w != IntPtr.Zero) return IntPtr.Zero;
        handled = true;
        return DefWindowProc(hwnd, msg, 1, -1); // lParam -1: don't repaint the classic frame (it shows as a white border)
    }

    /// Acrylic behind the (glass-extended, transparent) client area; off = no backdrop.
    public static void SetBlur(IntPtr hwnd, bool on)
    {
        var type = on ? 3 : 1; // DWMSBT_TRANSIENTWINDOW (acrylic) : DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, 38, ref type, 4); // DWMWA_SYSTEMBACKDROP_TYPE
    }

    public static void RoundedDarkFrame(IntPtr hwnd)
    {
        int dark = 1, round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
    }


    // ---- Global hotkey, clipboard listener, icon handles ----

    public const int WM_HOTKEY = 0x0312, WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);

    /// "Win+Shift+G" → MOD_* flags + virtual key. Needs at least one modifier.
    public static bool TryParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= 1; break;
                case "ctrl" or "control": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    // Enum.TryParse would read "1" as the numeric value 1 (Key.Cancel), so map digits to D1..D9 first.
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (int.TryParse(name, out _) || !Enum.TryParse<System.Windows.Input.Key>(name, true, out var key)) return false;
                    vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return mods != 0 && vk != 0;
    }

    // ---- TCP table with owning process: listening ports, open connections ----

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

    record TcpRow(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int State, int Pid);

    static List<TcpRow> TcpTable(int tableClass)
    {
        var rows = new List<TcpRow>();
        // AF_INET rows (MIB_TCPROW_OWNER_PID) are 24 bytes, AF_INET6 rows (MIB_TCP6ROW_OWNER_PID) 56.
        foreach (var (af, rowSize) in new[] { (2, 24), (23, 56) })
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, tableClass, 0);
            var buf = Marshal.AllocHGlobal(size += 4096); // slack: table can grow between the two calls
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, tableClass, 0) != 0) continue;
                var n = Marshal.ReadInt32(buf);
                for (var i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    int At(int offset) => Marshal.ReadInt32(row + offset);
                    rows.Add(af == 2
                        ? new(new IPAddress((uint)At(4)), Port(At(8)), new IPAddress((uint)At(12)), Port(At(16)), At(0), At(20))
                        : new(V6(row), Port(At(20)), V6(row + 24), Port(At(44)), At(48), At(52)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return rows;
    }

    static IPAddress V6(IntPtr at)
    {
        var ip = new byte[16];
        Marshal.Copy(at, ip, 0, 16);
        return new IPAddress(ip);
    }

    static Dictionary<int, string> ProcessNames() => Processes().ToDictionary(p => p.Pid, p => p.Name);

    // ---- Every process in one system call: no handle opened per process, and protected ones are included ----

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int needed);

    /// Io: bytes read + written so far, all files and devices (cached reads included), as Process Explorer's "I/O" counts it.
    public record struct ProcInfo(int Pid, string Name, TimeSpan Cpu, long WorkingSet, long Io);

    static int procBytes = 1 << 20; // what the list needed last time, plus slack

    /// Name (as Process.ProcessName gives it: no ".exe"), total CPU time and working set of every process.
    public static List<ProcInfo> Processes()
    {
        var list = new List<ProcInfo>(512);
        var size = procBytes;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            int status;
            // 5 = SystemProcessInformation; the list can outgrow the buffer between calls.
            while ((status = NtQuerySystemInformation(5, buf, size, out var needed)) == unchecked((int)0xC0000004) /* STATUS_INFO_LENGTH_MISMATCH */)
            {
                procBytes = size = needed + (64 << 10);
                buf = Marshal.ReAllocHGlobal(buf, size);
            }
            if (status != 0) return list;
            // SYSTEM_PROCESS_INFORMATION, x64 offsets (Glance is win-x64 only).
            for (var at = buf; ; at += Marshal.ReadInt32(at))
            {
                var pid = (int)Marshal.ReadInt64(at + 80);
                var name = pid == 0 ? "Idle" : Marshal.PtrToStringUni(Marshal.ReadIntPtr(at + 64), Marshal.ReadInt16(at + 56) / 2) ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                list.Add(new(pid, name, new TimeSpan(Marshal.ReadInt64(at + 40) + Marshal.ReadInt64(at + 48)) /* user + kernel, 100 ns */,
                    Marshal.ReadInt64(at + 144), Marshal.ReadInt64(at + 232) + Marshal.ReadInt64(at + 240) /* Read/WriteTransferCount */));
                if (Marshal.ReadInt32(at) == 0) break; // NextEntryOffset 0: last one
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    // ---- GPU use per process: the "GPU Engine" counters Task Manager reads ----

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, int format, ref int size, out int count, IntPtr items);

    static IntPtr gpuQuery, gpuCounter; // opened once; the wildcard also picks up processes started later

    /// GPU % per pid, averaged since the previous call: its busiest engine, as Task Manager counts it. Empty on the first call.
    /// Not thread-safe: the sampler loop is its only caller.
    public static Dictionary<int, double> GpuByPid()
    {
        var use = new Dictionary<int, double>();
        if (gpuQuery == 0 && PdhOpenQuery(null, 0, out gpuQuery) == 0)
            PdhAddEnglishCounter(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out gpuCounter);
        if (gpuCounter == 0 || PdhCollectQueryData(gpuQuery) != 0) return use;
        const int Double = 0x200; // PDH_FMT_DOUBLE
        var size = 0;
        if (PdhGetFormattedCounterArray(gpuCounter, Double, ref size, out _, 0) != unchecked((int)0x800007D2) /* PDH_MORE_DATA */) return use;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (PdhGetFormattedCounterArray(gpuCounter, Double, ref size, out var count, buf) != 0) return use;
            for (var i = 0; i < count; i++)
            {
                var item = buf + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: name, status, padding, double
                if (Marshal.ReadInt32(item + 8) is not (0 or 1)) continue; // valid / new data only
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? ""; // "pid_1234_luid_0x0_0xD1F5_phys_0_eng_0_engtype_3D"
                var end = name.IndexOf('_', 4);
                if (!name.StartsWith("pid_") || end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out var pid)) continue;
                use[pid] = Math.Max(use.GetValueOrDefault(pid), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return use;
    }

    static string Show(IPAddress ip) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();

    public static List<PortInfo> ListeningPorts()
    {
        var names = ProcessNames();
        return TcpTable(3 /* TCP_TABLE_OWNER_PID_LISTENER */).GroupBy(r => (r.LocalPort, r.Pid))
            .Select(g => new PortInfo(g.Key.LocalPort, string.Join(" ", g.Select(r => Show(r.Local)).Distinct()), g.Key.Pid,
                names.GetValueOrDefault(g.Key.Pid, "?")))
            .OrderBy(p => p.Port).ToList();
    }

    /// Established connections to other machines (loopback left out), by process.
    public static List<ConnInfo> Connections()
    {
        var names = ProcessNames();
        return TcpTable(4 /* TCP_TABLE_OWNER_PID_CONNECTIONS */)
            .Where(r => r.State == 5 /* ESTABLISHED */ && !IPAddress.IsLoopback(r.Remote))
            .Select(r => new ConnInfo(names.GetValueOrDefault(r.Pid, "?"), r.Pid,
                Show(r.Remote.IsIPv4MappedToIPv6 ? r.Remote.MapToIPv4() : r.Remote), r.RemotePort))
            .OrderBy(c => c.Process, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Remote).ToList();
    }

    // Port sits in the low 16 bits in network byte order.
    internal static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
