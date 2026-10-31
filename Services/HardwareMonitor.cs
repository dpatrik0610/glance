using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;

namespace Glance.Services;

public record Drive(string Name, double UsedGb, double TotalGb)
{
    public double Pct => UsedGb / TotalGb * 100;
    public string Text => $"{UsedGb:0} / {TotalGb:0} GB";
}

/// All processes sharing a name (chrome × 34), summed.
public record Proc(string Name, double Cpu, double MemMb, int Count)
{
    public bool CanKill => HardwareMonitor.CanKill(Name);
    public string CpuText => $"{Cpu:0.0}%";
    public string MemText => MemMb >= 1024 ? $"{MemMb / 1024:0.0} GB" : $"{MemMb:0} MB";
}

public record Snapshot(
    string CpuName, float? CpuLoad, float? CpuTemp, float? CpuClock, float? CpuPower,
    string GpuName, float? GpuLoad, float? GpuTemp, float? VramUsed, float? VramTotal, float? GpuPower, float? GpuFan,
    float? RamUsed, float? RamTotal, double NetDown, double NetUp,
    Drive[] Drives, Proc[] Procs);

// Not thread-safe: owned by the single sampler loop in App.
public sealed class HardwareMonitor
{
    readonly IHardware? cpu, gpu, mem;
    readonly ISensor[] cpuClocks;
    readonly Stopwatch clock = Stopwatch.StartNew();

    NetworkInterface[] nics = [];
    int nicAge = 30; // forces an adapter scan on the first read
    long lastRx, lastTx;
    TimeSpan lastNetAt, lastProcAt;
    Dictionary<int, TimeSpan> lastCpu = [];

    public HardwareMonitor()
    {
        var pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
        pc.Open();
        cpu = pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // Prefer a discrete GPU over the iGPU.
        gpu = pc.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderBy(h => h.HardwareType == HardwareType.GpuIntel).FirstOrDefault();
        mem = pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual"));
        cpuClocks = cpu?.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core")).ToArray() ?? [];

        Db.Info("sensors: " + string.Join("; ", new[] { cpu, gpu, mem }.OfType<IHardware>()
            .Select(h => $"{h.Name}[{string.Join(",", h.Sensors.Select(s => $"{s.SensorType}:{s.Name}"))}]")));
    }

    // detailed = UI is looking; also gather per-process and drive data (the expensive part).
    public Snapshot Read(bool detailed)
    {
        cpu?.Update(); gpu?.Update(); mem?.Update();
        var (down, up) = Net();
        var used = Get(mem, SensorType.Data, "Memory Used");
        var avail = Get(mem, SensorType.Data, "Memory Available");
        var clocks = cpuClocks.Where(s => s.Value > 0).Select(s => s.Value!.Value).ToArray();

        return new(
            cpu?.Name ?? "CPU",
            Get(cpu, SensorType.Load, "CPU Total"),
            Get(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core Average"),
            clocks.Length > 0 ? clocks.Max() : null,
            Nz(Get(cpu, SensorType.Power, "CPU Package", "Package")),
            gpu?.Name ?? "GPU",
            Get(gpu, SensorType.Load, "GPU Core", "D3D 3D"),
            Get(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot"),
            Get(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            Get(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"),
            Nz(Get(gpu, SensorType.Power, "GPU Package", "GPU Power")),
            Get(gpu, SensorType.Fan, "GPU Fan", "GPU"),
            used, used + avail,
            down, up,
            detailed ? Drives() : [],
            detailed ? Procs() : []);
    }

    static float? Get(IHardware? h, SensorType type, params string[] names)
    {
        if (h is null) return null;
        foreach (var n in names)
            foreach (var s in h.Sensors)
                if (s.SensorType == type && s.Name == n && s.Value is { } v) return v;
        return null;
    }

    // Power reads 0 without ring0 access; treat as unknown.
    static float? Nz(float? v) => v is > 0 ? v : null;

    (double down, double up) Net()
    {
        // Adapter enumeration is the costly bit; refresh the list every 30 samples.
        if (++nicAge > 30)
        {
            nicAge = 0;
            nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToArray();
            lastNetAt = default; // adapter set changed, so byte totals aren't comparable
        }
        long rx = 0, tx = 0;
        foreach (var n in nics)
        {
            try { var s = n.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; }
            catch (NetworkInformationException) { } // adapter vanished mid-sample
        }
        var now = clock.Elapsed;
        var dt = (now - lastNetAt).TotalSeconds;
        var r = lastNetAt == default ? (0d, 0d) : (Math.Max(0, (rx - lastRx) / dt), Math.Max(0, (tx - lastTx) / dt));
        (lastNetAt, lastRx, lastTx) = (now, rx, tx);
        return r;
    }

    static Drive[] Drives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => new Drive(d.Name.TrimEnd('\\'), (d.TotalSize - d.TotalFreeSpace) / 1e9, d.TotalSize / 1e9))
        .ToArray();

    Proc[] Procs()
    {
        var now = clock.Elapsed;
        var cur = new Dictionary<int, TimeSpan>(lastCpu.Count);
        var procs = Group(lastCpu, cur, (now - lastProcAt).TotalMilliseconds);
        (lastCpu, lastProcAt) = (cur, now);
        return procs;
    }

    /// CPU % of each name since `prev` was filled (0 for processes it hasn't seen) and working set; records this pass's CPU times in `cur`.
    static Proc[] Group(Dictionary<int, TimeSpan> prev, Dictionary<int, TimeSpan> cur, double elapsedMs)
    {
        var budgetMs = elapsedMs * Environment.ProcessorCount;
        var byName = new Dictionary<string, (double cpu, long mem, int count)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var t = p.TotalProcessorTime;
                    cur[p.Id] = t;
                    var c = prev.TryGetValue(p.Id, out var before) ? (t - before).TotalMilliseconds / budgetMs * 100 : 0;
                    byName.TryGetValue(p.ProcessName, out var a);
                    byName[p.ProcessName] = (a.cpu + c, a.mem + p.WorkingSet64, a.count + 1);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { } // protected or exited
            }
        }
        return byName.Select(kv => new Proc(kv.Key, kv.Value.cpu, kv.Value.mem / 1048576.0, kv.Value.count)).ToArray();
    }

    /// The heaviest process right now, for naming the culprit in an alert. CPU needs a second look a second later.
    public static async Task<Proc?> Top(bool byMemory)
    {
        var first = new Dictionary<int, TimeSpan>();
        var procs = Group([], first, 1);
        if (!byMemory)
        {
            var sw = Stopwatch.StartNew();
            await Task.Delay(1000);
            procs = Group(first, [], sw.Elapsed.TotalMilliseconds);
        }
        return procs.Where(p => p.Name is not ("Idle" or "Glance")).MaxBy(p => byMemory ? p.MemMb : p.Cpu);
    }

    // Ending these crashes or logs out Windows (or is Glance itself); Task Manager warns about the same set.
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "LsaIso", "svchost", "dwm", "fontdrvhost", "sihost", "Glance",
    };

    public static bool CanKill(string name) => !Critical.Contains(name);

    /// Ends every process with this name in the user's session. Returns what went wrong, or null.
    public static string? Kill(string name)
    {
        if (!CanKill(name)) return $"{name} is part of Windows.";
        var session = Process.GetCurrentProcess().SessionId;
        var denied = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try { if (p.SessionId == session) p.Kill(); }
                catch (Win32Exception) { denied++; } // elevated or protected
                catch (InvalidOperationException) { } // already gone
            }
        }
        Db.Info($"kill {name} denied={denied}");
        return denied == 0 ? null : $"{denied} {name} process{(denied == 1 ? "" : "es")} refused to end"
            + (Native.IsAdmin ? "." : ". Restart Glance as admin to end elevated processes.");
    }
}
