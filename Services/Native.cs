using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Glance.Services;

public record PortInfo(int Port, string Address, int Pid, string Process);
public record ConnInfo(string Process, int Pid, string Remote, int Port);

public static class Native
{
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

    static Dictionary<int, string> ProcessNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses()) using (p) names[p.Id] = p.ProcessName;
        return names;
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
