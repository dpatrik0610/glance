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

public record Proc(string Name, double Cpu, double MemMb)
{
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
        var budgetMs = (now - lastProcAt).TotalMilliseconds * Environment.ProcessorCount;
        var cur = new Dictionary<int, TimeSpan>(lastCpu.Count);
        var byName = new Dictionary<string, (double cpu, long mem)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var t = p.TotalProcessorTime;
                    cur[p.Id] = t;
                    var c = lastCpu.TryGetValue(p.Id, out var prev) ? (t - prev).TotalMilliseconds / budgetMs * 100 : 0;
                    byName.TryGetValue(p.ProcessName, out var a);
                    byName[p.ProcessName] = (a.cpu + c, a.mem + p.WorkingSet64);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { } // protected or exited
            }
        }
        (lastCpu, lastProcAt) = (cur, now);
        return byName.OrderByDescending(kv => kv.Value.cpu).ThenByDescending(kv => kv.Value.mem).Take(7)
            .Select(kv => new Proc(kv.Key, kv.Value.cpu, kv.Value.mem / 1048576.0)).ToArray();
    }
}
